using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Company.Function
{
    public class CheckGDriveForNewFiles
    {
        private readonly ILogger _logger;

        public CheckGDriveForNewFiles(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<CheckGDriveForNewFiles>();
        }

        [Function("check_gdrive_for_new_files")]
        public async Task RunAsync([TimerTrigger("0 */10 * * * *")] TimerInfo myTimer)
        {
            _logger.LogInformation($"C# Timer trigger function executed at: {DateTime.UtcNow}");
            if (myTimer.ScheduleStatus is not null)
            {
                _logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus.Next}");
            }

            try
            {
                // Fetch values from environment variables
                string rootFolderId = Environment.GetEnvironmentVariable("ROOT_FOLDER_ID");
                string applicationName = Environment.GetEnvironmentVariable("APPLICATION_NAME");
                string apiEndpoint = Environment.GetEnvironmentVariable("API_ENDPOINT");
                
                var credentialsFilePath = GetDecodedCredentials();    
                
                // Validate environment variables
                if (string.IsNullOrEmpty(rootFolderId))
                {
                    _logger.LogError("ROOT_FOLDER_ID is not set in the environment variables.");
                    return;
                }

                if (string.IsNullOrEmpty(applicationName))
                {
                    _logger.LogError("APPLICATION_NAME is not set in the environment variables.");
                    return;
                }

                if (string.IsNullOrEmpty(apiEndpoint))
                {
                    _logger.LogError("API_ENDPOINT is not set in the environment variables.");
                    return;
                }

                if (string.IsNullOrEmpty(credentialsFilePath) || !File.Exists(credentialsFilePath))
                {
                    _logger.LogInformation($"CREDENTIALS_FILE_PATH: {credentialsFilePath}");
                    _logger.LogError("CREDENTIALS_FILE_PATH is not set or the file does not exist.");
                    return;
                }

                // Initialize Google Drive API service
                string[] Scopes = { DriveService.Scope.DriveReadonly };
                var credential = GoogleCredential.FromFile(credentialsFilePath).CreateScoped(Scopes);

                var service = new DriveService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credential,
                    ApplicationName = applicationName,
                });

                var allFiles = new List<Google.Apis.Drive.v3.Data.File>();

                // Fetch subfolders in the root folder
                var subfolders = await GetSubfoldersAsync(service, rootFolderId);

                if (subfolders.Count == 0)
                {
                    _logger.LogInformation("No subfolders found in the root folder. Exiting.");
                    return;
                }

                // Traverse each subfolder for files
                foreach (var subfolder in subfolders)
                {
                    _logger.LogInformation($"Starting traversal for subfolder: {subfolder.Name} (ID: {subfolder.Id})");
                    await TraverseFolderAsync(service, subfolder.Id, allFiles);
                }

                if (allFiles.Count > 0)
                {
                    _logger.LogInformation($"{allFiles.Count} file(s) found in Google Drive.");
                    foreach (var file in allFiles)
                    {
                        _logger.LogInformation($"Found file: {file.Name}");
                    }

                    // Call the REST API endpoint
                    await CallRestApiAsync(apiEndpoint, allFiles);
                }
                else
                {
                    _logger.LogInformation("No files found in Google Drive.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unexpected error: {ex.Message}");
            }
        }

        private async Task<IList<Google.Apis.Drive.v3.Data.File>> GetSubfoldersAsync(DriveService service, string rootFolderId)
        {
            var folderRequest = service.Files.List();
            folderRequest.Q = $"'{rootFolderId}' in parents and mimeType = 'application/vnd.google-apps.folder'";
            folderRequest.Fields = "nextPageToken, files(id, name)";

            var folderResult = await folderRequest.ExecuteAsync();

            if (folderResult.Files != null && folderResult.Files.Count > 0)
            {
                _logger.LogInformation($"{folderResult.Files.Count} subfolder(s) found in root folder.");
                foreach (var subfolder in folderResult.Files)
                {
                    _logger.LogInformation($"Subfolder found: {subfolder.Name} (ID: {subfolder.Id})");
                }
                return folderResult.Files;
            }

            _logger.LogInformation("No subfolders found in root folder.");
            return new List<Google.Apis.Drive.v3.Data.File>();
        }

        private string GetDecodedCredentials()
        {
            var base64Credentials = Environment.GetEnvironmentVariable("CREDENTIALS");
            if (string.IsNullOrEmpty(base64Credentials))
            {
                throw new InvalidOperationException("CREDENTIALS_BASE64 environment variable is not set.");
            }

            // Decode the base64 string
            var jsonCredentials = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Credentials));

            // Optionally write to a temp file if needed
            var tempFilePath = Path.Combine(Path.GetTempPath(), "service-account.json");
            File.WriteAllText(tempFilePath, jsonCredentials);
            _logger.LogInformation($"Credentials written to temp file: {tempFilePath}");

            return tempFilePath;
        }

        private async Task TraverseFolderAsync(DriveService service, string folderId, List<Google.Apis.Drive.v3.Data.File> allFiles)
        {
            _logger.LogInformation($"Checking folder ID: {folderId}");

            try
            {
                // Query to find files in the folder
                var fileRequest = service.Files.List();
                fileRequest.Q = $"'{folderId}' in parents and mimeType != 'application/vnd.google-apps.folder'";
                fileRequest.Fields = "nextPageToken, files(id, name)";

                var fileResult = await fileRequest.ExecuteAsync();

                if (fileResult.Files != null && fileResult.Files.Count > 0)
                {
                    _logger.LogInformation($"{fileResult.Files.Count} file(s) found in folder ID: {folderId}");
                    foreach (var file in fileResult.Files)
                    {
                        _logger.LogInformation($"File found - ID: {file.Id}, Name: {file.Name}");
                    }
                    allFiles.AddRange(fileResult.Files);
                }
                else
                {
                    _logger.LogInformation($"No files found in folder ID: {folderId}");
                }

                // Query to find subfolders in the folder
                var folderRequest = service.Files.List();
                folderRequest.Q = $"'{folderId}' in parents and mimeType = 'application/vnd.google-apps.folder'";
                folderRequest.Fields = "nextPageToken, files(id, name)";

                var folderResult = await folderRequest.ExecuteAsync();

                if (folderResult.Files != null && folderResult.Files.Count > 0)
                {
                    foreach (var subfolder in folderResult.Files)
                    {
                        _logger.LogInformation($"Subfolder found - ID: {subfolder.Id}, Name: {subfolder.Name}");
                        await TraverseFolderAsync(service, subfolder.Id, allFiles); // Recursively check subfolder
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while accessing folder ID {folderId}: {ex.Message}");
            }
        }

        private async Task CallRestApiAsync(string endpointUrl, List<Google.Apis.Drive.v3.Data.File> files)
        {
            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(2);

                    var payload = new
                    {
                        timestamp = DateTime.UtcNow,
                        files = files.Select(f => new { f.Id, f.Name }).ToList()
                    };

                    var jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);
                    var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                    var response = await httpClient.PostAsync(endpointUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("REST API call was successful.");
                    }
                    else
                    {
                        _logger.LogError($"Failed to call REST API. Status Code: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                    }
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogError("REST API call timed out after 2 seconds.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while calling REST API: {ex.Message}");
            }
        }
    }
}
