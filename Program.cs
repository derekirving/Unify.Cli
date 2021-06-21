using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Graph;
using Spectre.Console;
using Unify.Cli.Helpers;
using Unify.Cli.Models;
using Unify.Models;
using Application = Unify.Models.Application;
using File = System.IO.File;
using Root = Unify.Models.Root;

namespace Unify.Cli
{
    internal class Program
    {
        public static async Task Main(string[] args)
        {
            var app = new CommandLineApplication
            {
                Name = "unify",
                Description = "A tool to configure Unify applications"
            };

            app.HelpOption(true);

            app.Command("config", configCmd =>
            {
                configCmd.ExtendedHelpText = "Use this to initially configure a unify app";
                configCmd.OnExecute(Configure);
            });
            
            app.Command("azure-ad", configCmd =>
            {
                configCmd.ExtendedHelpText = "Use this to provision Azure AD authentication for this app";
                configCmd.OnExecuteAsync(async _ =>
                {
                    var appDone = await Provision();
                    if(appDone != null)
                        WriteProvisionedInfo(appDone);
                });
            });
            
            app.Command("un-azure-ad", configCmd =>
            {
                configCmd.ExtendedHelpText = "Use this to remove the Azure AD app registration from the Azure Portal";
                configCmd.OnExecuteAsync(async _ =>
                {
                    var projectPath = await Unregister();
                    RemoveProvisionedInfo(projectPath);
                });
            });

            app.OnExecute(() =>
            {
                var cancellationTokenSource = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cancellationTokenSource.Cancel();
                };
                
                AnsiConsole.Render(
                    new FigletText("Unify")
                        .LeftAligned()
                        .Color(Color.Red));

                var versionString = Assembly.GetEntryAssembly()!
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                
                AnsiConsole.MarkupLine($"\nUnify.Cli version [bold yellow]{versionString}[/]");
                AnsiConsole.MarkupLine("Derek Irving, University of Strathclyde Business School");

                app.ShowHelp();
                return 1;
            });

            await app.ExecuteAsync(args);
        }

        private static void Configure()
        {
            var currentDirectory = Environment.CurrentDirectory;
            AnsiConsole.MarkupLine($"Starting Config in [underline green]{currentDirectory}[/]");
            
            var projectFile = ProjectHelpers.GetCurrentProject(currentDirectory);

            if (string.IsNullOrEmpty(projectFile))
            {
                AnsiConsole.MarkupLine($"{Emoji.Known.CrossMark} [bold red]No project file in[/] {currentDirectory}");
                return;
            }

            if (File.Exists(Path.Combine(currentDirectory, "unify.app.id")))
            {
                AnsiConsole.MarkupLine("[bold red3]It looks like this app has already been configured for Unify...[/]");

                if (!AnsiConsole.Confirm("Do you want to overwrite the existing configuration?"))
                {
                    return;
                }
            }

            var existingUserSecretsFile = ProjectHelpers.GetExistingUserSecretsNode(projectFile);
            bool userSecretsExist = false;
            if (!string.IsNullOrEmpty(existingUserSecretsFile))
            {
                AnsiConsole.MarkupLine("[bold red3]It looks like UserSecrets have already been configured for this app...[/]");

                if (!AnsiConsole.Confirm("If you proceed, the UserSecretsId for this app will change.\nDo you want to overwrite the existing configuration?"))
                {
                    return;
                }
                userSecretsExist = true;
            }

            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var id = ProjectHelpers.GenerateId(projectName);
            var masterKey = ProjectHelpers.GenerateMasterKey();
            var (rsaPublicKey, rsaPrivateKey) = ProjectHelpers.GenerateKeys(2048);

            var unifyApplication = new Root
            {
                Unify = new Unify.Models.Unify
                {
                   Application = new Application
                    {
                        Id = id,
                        MasterKey = masterKey,
                        RsaKeys = new RsaKeys {PublicKey = rsaPublicKey, PrivateKey = rsaPrivateKey}
                    }
                }
            };

            if (userSecretsExist)
            {
                ProjectHelpers.SetUserSecrets(projectFile, id, true);
            }
            else
            {
                ProjectHelpers.SetUserSecrets(projectFile, id);
            }
            
            File.WriteAllText(Path.Combine(currentDirectory, "unify.app.id"), id);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var jsonString = JsonSerializer.Serialize(unifyApplication, options);
            var (dir, tree) = ProjectHelpers.CreateOrUpdateUserSecrets(id, projectFile, jsonString);
            
            AnsiConsole.MarkupLine($"[bold green]Configuration created at {dir} [/]");

            var splitPath = tree.Split(Path.DirectorySeparatorChar);
            var root = new Tree(splitPath[0]);
                
            TreeNode currentNode = null;
            for (var i = 0; i < splitPath.Length-1; i++)
            {
                var item = splitPath[i+1];
                currentNode = root.Nodes.Count > 0 ? currentNode.AddNode(item) : root.AddNode(item);
            }

            AnsiConsole.Render(root);
        }
        
        private static async Task<string> Unregister()
        {
            var currentDirectory = Environment.CurrentDirectory;

            var projectFile = ProjectHelpers.GetCurrentProject(currentDirectory);
            
            var existingSecrets = await ProjectHelpers.LoadUserSecrets(projectFile);
            
            var configObject = await ProjectHelpers.GetGlobalConfig();
            
            var tenantId = configObject.Unify.Identity.AzureAd.TenantId;
            
            TokenCredential tokenCredential = new MsalTokenCredential(
                tenantId,
                "");
            
            var graphServiceClient = new GraphServiceClient(new TokenCredentialAuthenticationProvider(tokenCredential));
            
            var apps = await graphServiceClient.Applications
                .Request()
                .Filter($"appId eq '{existingSecrets.Unify.Identity.AzureAd.ClientId}'")
                .GetAsync();
            
            var readApplication = apps.FirstOrDefault();
            if (readApplication != null)
            {
                await graphServiceClient.Applications[$"{readApplication.Id}"]
                    .Request()
                    .DeleteAsync();
            }
            
            return projectFile;
        }

        private static async Task<ConfiguredApplication> Provision()
        {
            var currentDirectory = Environment.CurrentDirectory;

            var projectFile = ProjectHelpers.GetCurrentProject(currentDirectory);

            if (string.IsNullOrEmpty(projectFile))
            {
                AnsiConsole.MarkupLine($"{Emoji.Known.CrossMark} [bold red]No project file in {currentDirectory}[/]");
                return null;
            }

            var existingSecrets = await ProjectHelpers.LoadUserSecrets(projectFile);
            if (existingSecrets?.Unify?.Identity?.AzureAd?.ClientId != null)
            {
                AnsiConsole.MarkupLine("[bold red3]It looks like AzureAd has already been configured for this app...[/]");
                
                if (!AnsiConsole.Confirm("If you proceed, the AzureAd configuration for this app will change.\nDo you want to overwrite the existing configuration?"))
                {
                    return null;
                }
            }
            
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            
            AnsiConsole.MarkupLine($"Starting Azure Ad provisioning for [underline green]{projectName}[/]");

            var configObject = await ProjectHelpers.GetGlobalConfig();
            
            if(configObject == null)
                throw new Exception($"Could not get global config");
            
            var tenantId = configObject.Unify.Identity.AzureAd.TenantId;
            
            TokenCredential tokenCredential = new MsalTokenCredential(
                tenantId,
                "");
            
            var graphServiceClient = new GraphServiceClient(new TokenCredentialAuthenticationProvider(tokenCredential));
            
            var application = new Microsoft.Graph.Application
            {
                DisplayName = projectName,
                SignInAudience = "AzureADMyOrg",
                Description = "Automatically configured by the Unify.Cli",
                Web = new WebApplication
                {
                    RedirectUris =
                        new[]
                        {
                            "https://localhost:44343/signin-oidc",
                            "https://www.sbs.strath.ac.uk/" + projectName + "/signin-oidc"
                        },
                    LogoutUrl = "https://www.sbs.strath.ac.uk/" + projectName + "/signout-callback-oidc",
                    ImplicitGrantSettings = new ImplicitGrantSettings {EnableIdTokenIssuance = true}
                }
            };
            
            var createdApplication = await graphServiceClient.Applications
                .Request()
                .AddAsync(application);
            
            var passwordCredential = new PasswordCredential
            {
                DisplayName = $"Password created by the Unify Cli for project \"{projectName}\""
            };
            
            var returnedPasswordCredential = await graphServiceClient.Applications[$"{createdApplication.Id}"]
                .AddPassword(passwordCredential)
                .Request()
                .PostAsync();
            
            var configuredApplication = new ConfiguredApplication
            {
                ClientId = createdApplication.AppId,
                ClientSecret = returnedPasswordCredential.SecretText,
                ProjectPath = projectFile
            };
            
            return configuredApplication;
        }

        private static void WriteProvisionedInfo(ConfiguredApplication configuredApplication)
        {
            var userSecretsId = ProjectHelpers.GetExistingUserSecretsNode(configuredApplication.ProjectPath);
            
            if (string.IsNullOrEmpty(userSecretsId))
            {
                var projectName = Path.GetFileNameWithoutExtension(configuredApplication.ProjectPath);
                userSecretsId = ProjectHelpers.GenerateId(projectName);
                ProjectHelpers.SetUserSecrets(configuredApplication.ProjectPath, userSecretsId);
            }
            
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            
            var unifyApplication = new Root
            {
                Unify = new Unify.Models.Unify
                {
                    Identity = new Unify.Models.Identity
                    {
                        AzureAd = new AzureAd
                        {
                            ClientId = configuredApplication.ClientId,
                            ClientSecret = configuredApplication.ClientSecret
                        }
                    }
                }
            };

            var jsonString = JsonSerializer.Serialize(unifyApplication, options);
            
            var userSecretPath =
                ProjectHelpers.CreateOrUpdateUserSecrets(userSecretsId, configuredApplication.ProjectPath,
                    jsonString);
            
            AnsiConsole.MarkupLine($"Configured client with id [underline green]{configuredApplication.ClientId}[/]");
            AnsiConsole.MarkupLine($"Project Path: {configuredApplication.ProjectPath}");
            AnsiConsole.MarkupLine($"Secrets Path: {userSecretPath.Item2}");
        }

        private static void RemoveProvisionedInfo(string projectFile)
        {
            AnsiConsole.MarkupLine($"Removed from the Azure Portal.\n You will need to manually remove AzureAd config from your User Secrets");
        }
    }
}