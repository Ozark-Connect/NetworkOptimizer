using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

public class PerfTweakSetting
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string TweakId { get; set; } = "";

    public bool IsManuallyDeployed { get; set; }
}
