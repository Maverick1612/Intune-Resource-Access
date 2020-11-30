// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using Microsoft.Management.Services.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Microsoft.Intune
{
    /// <summary>
    /// API to download CARevocationRequests that were generated by Intune
    /// </summary>
    public class IntuneRevocationClient
    {
        public const string DEFAULT_SERVICE_VERSION = "5019-05-05";
        public const string CAREQUEST_SERVICE_NAME = "PkiConnectorFEService";
        public const string DOWNLOADREVOCATIONREQUESTS_URL = "CertificateAuthorityRequests/downloadRevocationRequests";
        public const string UPLOADREVOCATIONRESULTS_URL = "CertificateAuthorityRequests/uploadRevocationResults";
        public const int MAXREQUESTS_MAXVALUE = 500;

        private TraceSource trace = new TraceSource(nameof(IntuneRevocationClient));

        /// <summary>
        /// The version of the PkiConnectorFEService that we are making requests against.
        /// </summary>
        private string serviceVersion = null;

        /// <summary>
        /// The CertificateAuthority Identifier to be used in log correlation on Intune.
        /// </summary>
        private string providerNameAndVersion = null;

        /// <summary>
        /// The Intune client to use to make requests to Intune services.
        /// </summary>
        private IIntuneClient intuneClient = null;

        /// <summary>
        /// Creates an new instance of IntuneRevocationClient
        /// </summary>
        /// <param name="configProperties">Config dictionary containing properties for the client.</param>
        /// <param name="trace">Trace</param>
        /// <param name="intuneClient">IntuneClient to use to make requests to intune.</param>
        [SuppressMessage("Microsoft.Usage", "CA2208", Justification = "Using a parameter coming from an object.")]
        public IntuneRevocationClient(
            Dictionary<string,string> configProperties,
            TraceSource trace = null,
            IIntuneClient intuneClient = null)
        {
            // Required Parameters
            if (configProperties == null)
            {
                throw new ArgumentNullException(nameof(configProperties));
            }

            configProperties.TryGetValue("PROVIDER_NAME_AND_VERSION", out this.providerNameAndVersion);
            if (string.IsNullOrWhiteSpace(providerNameAndVersion))
            {
                throw new ArgumentNullException(nameof(providerNameAndVersion));
            }

            // Optional Parameters
            if (trace != null)
            {
                this.trace = trace;
            }

            configProperties.TryGetValue("PkiConnectorFEServiceVersion", out this.serviceVersion);
            serviceVersion = serviceVersion ?? DEFAULT_SERVICE_VERSION;

            // Dependencies
            var adalClient = new AdalClient(
                        // Required
                        configProperties,
                        // Overrides
                        trace: trace
                        );

            this.intuneClient = intuneClient ?? new IntuneClient(
                    // Required    
                    configProperties,
                    // Overrides
                    trace: trace,
                    // Dependencies
                    adalClient: adalClient,
                    locationProvider: new IntuneServiceLocationProvider(
                        // Required
                        configProperties,
                        // Overrides
                        trace: trace,
                        // Dependencies
                        authClient: adalClient
                        )
                    );
        }

        /// <summary>
        /// Downloads a list of CARevocationRequests from Intune to be acted on
        /// </summary>
        /// <param name="transactionId">Transaction Id for request</param>
        /// <param name="maxCARevocationRequestsToDownload">The maximum number of requests to download</param>
        /// <param name="certificateProviderName">Optional filter for the name of the Certificate Authority</param>
        /// <param name="issuerName">Optional filter for the issuer name</param>
        /// <returns>List of CARevocationRequests</returns>
        public async Task<List<CARevocationRequest>> DownloadCARevocationRequestsAsync(string transactionId, int maxCARevocationRequestsToDownload, string certificateProviderName = null, string issuerName = null)
        {
            // Validate the parameters
            if (string.IsNullOrWhiteSpace(transactionId))
            {
                throw new ArgumentNullException(nameof(transactionId));
            }
            if (maxCARevocationRequestsToDownload <= 0 || maxCARevocationRequestsToDownload > MAXREQUESTS_MAXVALUE)
            {
                throw new ArgumentOutOfRangeException($"{nameof(maxCARevocationRequestsToDownload)} should be between 1 and {MAXREQUESTS_MAXVALUE}. {nameof(maxCARevocationRequestsToDownload)} value Requested: {maxCARevocationRequestsToDownload}.");
            }

            // Create CARevocationDownloadParameters request body to send to Intune
            var downloadParamsObj = new CARevocationDownloadParameters()
            {
                MaxRequests = maxCARevocationRequestsToDownload,
                CertificateProviderName = certificateProviderName, 
                IssuerName = issuerName,
            };
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings()
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };
            JObject requestBody = new JObject(new JProperty("downloadParameters", JToken.FromObject(downloadParamsObj)));

            // Perform Download call
            JObject result = await PostAsync(requestBody, DOWNLOADREVOCATIONREQUESTS_URL, transactionId);
            
            // Deserialize the results from the download call
            List<CARevocationRequest> revocationRequests;
            try
            {
                if (result == null || result["value"] == null)
                {
                    throw new IntuneClientException($"Unable to deeserialize value returned from Intune. No 'value' property is present in the resposne. JSON: {result}.");
                }

                revocationRequests = (List<CARevocationRequest>)result["value"].ToObject(typeof(List<CARevocationRequest>));
            }
            catch (JsonException e)
            {
                throw new IntuneClientException($"Unable to deeserialize value returned from Intune. Value: {result}. Exception: {e}");
            }

            return revocationRequests;
        }

        /// <summary>
        /// Send a list of Revocation results to Intune
        /// </summary>
        /// <param name="transactionId">The transactionId</param>
        /// <param name="requestResults">List of CARevocationResult to send to Intune</param>
        public async Task UploadRevocationResultsAsync(string transactionId, List<CARevocationResult> requestResults)
        {
            // Validate the parameters
            if (string.IsNullOrWhiteSpace(transactionId))
            {
                throw new ArgumentNullException(nameof(transactionId));
            }
            if (requestResults == null || requestResults.Count == 0)
            {
                throw new ArgumentNullException(nameof(requestResults));
            }

            // Create the Request body containing the results to send to Intune
            JObject requestBody = new JObject(new JProperty("results", JToken.FromObject(requestResults)));

            // Perform the Upload results call 
            JObject result = await PostAsync(requestBody, UPLOADREVOCATIONRESULTS_URL, transactionId);

            if (result == null || result["value"] == null)
            {
                throw new IntuneClientException($"Unable to deeserialize value returned from Intune. No 'value' property is present in the resposne. JSON: {result}.");
            }

            // Parse the result being sent back from Intune
            if (!bool.TryParse((string)result["value"], out bool postSuccessful) || !postSuccessful)
            {
                throw new IntuneClientException($"Results not successfully recorded in Intune. Expected 'true' from service. Recieved: '{result}'");
            }
        }

        /// <summary>
        /// Calls PostAsync on the Intune Client
        /// </summary>
        /// <param name="requestBody">Request body to include in the HTTP call</param>
        /// <param name="urlSuffix">URL suffix to use in the POST call</param>
        /// <param name="transactionId">Transaction Id</param>
        /// <returns></returns>
        private async Task<JObject> PostAsync(JObject requestBody, string urlSuffix, string transactionId)
        {
            Guid activityId = Guid.NewGuid();

            JObject resultJson = await intuneClient.PostAsync(CAREQUEST_SERVICE_NAME,
                    urlSuffix,
                    serviceVersion,
                    requestBody,
                    activityId);
            trace.TraceEvent(TraceEventType.Information, 0, $"Activity {activityId} has completed for transaction id {transactionId}.");
            trace.TraceEvent(TraceEventType.Information, 0, $"Result Returned: {resultJson}");
            
            return resultJson;
        }
    }
}