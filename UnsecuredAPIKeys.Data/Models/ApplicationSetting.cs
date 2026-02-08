using System.ComponentModel.DataAnnotations;

namespace UnsecuredAPIKeys.Data.Models
{
    public class ApplicationSetting
    {
        [Key] public required string Key { get; init; }
        public required string Value { get; init; }
        public string? Description { get; init; }
    }
}
