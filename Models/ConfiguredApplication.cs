using System.Text.Json.Serialization;

namespace Unify.Cli.Models
{
    public class ConfiguredApplication
    {
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        [JsonIgnore]
        public string ProjectPath { get; set; }
    }
}