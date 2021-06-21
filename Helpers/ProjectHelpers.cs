using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Unify.Cli.EasyCrypto;
using Unify.Cli.Extensions;
using Unify.Models;

namespace Unify.Cli.Helpers
{
    public class ProjectHelpers
    {
        public static string GenerateId(string projectName)
        {
            var gen = new IdGenerator(projectName, true) {AddHyphens = true};
            return gen.NewId(DateTime.UtcNow);
        }

        public static string GenerateMasterKey()
        {
            using var rngCsp = new RNGCryptoServiceProvider();
            var randomData = new byte[16];
            rngCsp.GetBytes(randomData);
            return randomData.ToBase16();
        }

        public static (string publicKey, string privateKey) GenerateKeys(int keyLength)
        {
            using var rsa = RSA.Create();
            rsa.KeySize = keyLength;
            return (
                publicKey: rsa.ToXmlString(false),
                privateKey: rsa.ToXmlString(true));
        }

        public static string GetCurrentProject(string currentDirectory)
        {
            var csProjFiles = Directory.EnumerateFiles(currentDirectory, "*.csproj");
            var projFiles = csProjFiles as string[] ?? csProjFiles.ToArray();
            return !projFiles.Any() ? string.Empty : projFiles.First();
        }

        public static async Task<Root> GetGlobalConfig()
        {
            var remotePath = Environment.GetEnvironmentVariable(Constants.UnifyPathEnvironment);
            
            if (string.IsNullOrEmpty(remotePath))
                remotePath = Environment.GetEnvironmentVariable(Constants.UnifyPathEnvironment, EnvironmentVariableTarget.User);
            
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"There is no {Constants.UnifyPathEnvironment} environment variable");
            
            var globalConfigPath = Path.Combine(remotePath, Constants.UnifyGlobalSecretsFile);
            
            var dirInfo = new DirectoryInfo(globalConfigPath);
            
            if (dirInfo == null)
                throw new Exception($"Could not access path {globalConfigPath}");
            
            var json = await File.ReadAllTextAsync(dirInfo.FullName);
            
            return JsonSerializer.Deserialize<Root>(json, new JsonSerializerOptions{PropertyNameCaseInsensitive = true});
        }

        public static string GetUserSecretsPath()
        {
            var root =
                Environment.GetEnvironmentVariable("APPDATA") // On Windows it goes to %APPDATA%\Microsoft\UserSecrets\
                ?? Environment.GetEnvironmentVariable("HOME") // On Mac/Linux it goes to ~/.microsoft/usersecrets/
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException(
                    "Could not determine an appropriate location for storing user secrets.");

            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPDATA"))
                ? Path.Combine(root, "Microsoft", "UserSecrets")
                : Path.Combine(root, ".microsoft", "usersecrets");
        }

        public static string GetExistingUserSecretsNode(string projectPath)
        {
            var projectDocument = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            var existingUserSecretsId = projectDocument.XPathSelectElements("//UserSecretsId").FirstOrDefault();
            return existingUserSecretsId is object ? existingUserSecretsId.Value : null;
        }

        public static void SetUserSecrets(string projectPath, string secretsId, bool update = false)
        {
            var projectDocument = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);

            if (update)
            {
                var existingUserSecretsId = projectDocument.XPathSelectElements("//UserSecretsId").FirstOrDefault();
                existingUserSecretsId.Value = secretsId;
            }
            else
            {
                var propertyGroup = projectDocument.Root.DescendantNodes()
                    .FirstOrDefault(node => node is XElement el
                                            && el.Name == "PropertyGroup"
                                            && el.Attributes().All(attr =>
                                                attr.Name != "Condition")) as XElement;

                // No valid property group, create a new one
                if (propertyGroup == null)
                {
                    propertyGroup = new XElement("PropertyGroup");
                    projectDocument.Root.AddFirst(propertyGroup);
                }

                // Add UserSecretsId element
                propertyGroup.Add("  ");
                propertyGroup.Add(new XElement("UserSecretsId", secretsId));
                propertyGroup.Add($"{Environment.NewLine}  ");
            }

            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
            };

            using var xw = XmlWriter.Create(projectPath, settings);
            projectDocument.Save(xw);
        }

        public static async Task<Root> LoadUserSecrets(string projectFile)
        {
            var id = GetExistingUserSecretsNode(projectFile);
            var userSecretPath = Path.Combine(GetUserSecretsPath(), id, "secrets.json");
            if (!File.Exists(userSecretPath)) return null;
            
            var config = await File.ReadAllTextAsync(userSecretPath);
            return JsonSerializer.Deserialize<Root>(config);
        }

        public static (string,string) CreateOrUpdateUserSecrets(string id, string projectFile, string jsonString)
        {
            var existingUserSecretsFile = GetExistingUserSecretsNode(projectFile);
            
            var userSecretPath = Path.Combine(GetUserSecretsPath(), id);
            var appSecretsPath = Path.Combine(userSecretPath, "secrets.json");
            
            if (!string.IsNullOrEmpty(existingUserSecretsFile))
            {
                var existingDirectory = Path.Combine(ProjectHelpers.GetUserSecretsPath(), existingUserSecretsFile);

                if (Directory.Exists(existingDirectory))
                {
                    var existingJsonFile = Path.Combine(existingDirectory, "secrets.json");
                    if (File.Exists(existingJsonFile))
                    {
                        var existingJson = File.ReadAllText(existingJsonFile);
                        jsonString = JsonHelpers.MergeJson(existingJson, jsonString);
                    }

                    if (!existingDirectory.Equals(userSecretPath))
                    {
                        Directory.Move(existingDirectory, userSecretPath);
                    }
                }
                else
                {
                    Directory.CreateDirectory(userSecretPath);
                }
            }
            else
            {
                Directory.CreateDirectory(userSecretPath);
            }

            File.WriteAllText(appSecretsPath, jsonString);

            return (userSecretPath, appSecretsPath);
        }
    }
}