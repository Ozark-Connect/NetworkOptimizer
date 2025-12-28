using Microsoft.Extensions.Logging;
using NetworkOptimizer.Agents.Models;
using Scriban;
using Scriban.Runtime;

namespace NetworkOptimizer.Agents;

/// <summary>
/// Renders script templates with configuration values using Scriban
/// </summary>
public class ScriptRenderer
{
    private readonly ILogger<ScriptRenderer> _logger;
    private readonly string _templatesPath;

    public ScriptRenderer(ILogger<ScriptRenderer> logger, string? templatesPath = null)
    {
        _logger = logger;
        _templatesPath = templatesPath ?? Path.Combine(AppContext.BaseDirectory, "Templates");
    }

    /// <summary>
    /// Renders a template file with the given configuration
    /// </summary>
    public async Task<string> RenderTemplateAsync(string templateName, AgentConfiguration config)
    {
        try
        {
            var templatePath = Path.Combine(_templatesPath, templateName);

            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException($"Template not found: {templatePath}");
            }

            var templateContent = await File.ReadAllTextAsync(templatePath);
            var template = Template.Parse(templateContent);

            if (template.HasErrors)
            {
                var errors = string.Join(", ", template.Messages.Select(m => m.Message));
                throw new InvalidOperationException($"Template parsing errors: {errors}");
            }

            var scriptObject = BuildScriptObject(config);
            var rendered = await template.RenderAsync(scriptObject);

            _logger.LogDebug("Successfully rendered template {Template} for agent {AgentId}",
                templateName, config.AgentId);

            return rendered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render template {Template}", templateName);
            throw;
        }
    }

    /// <summary>
    /// Renders a template from a string rather than a file
    /// </summary>
    public async Task<string> RenderTemplateStringAsync(string templateContent, AgentConfiguration config)
    {
        try
        {
            var template = Template.Parse(templateContent);

            if (template.HasErrors)
            {
                var errors = string.Join(", ", template.Messages.Select(m => m.Message));
                throw new InvalidOperationException($"Template parsing errors: {errors}");
            }

            var scriptObject = BuildScriptObject(config);
            return await template.RenderAsync(scriptObject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render template string");
            throw;
        }
    }

    /// <summary>
    /// Gets the appropriate template names for an agent type
    /// </summary>
    public List<string> GetTemplatesForAgent(AgentType agentType)
    {
        return agentType switch
        {
            AgentType.UDM or AgentType.UCG => new List<string>
            {
                "udm-agent-boot.sh.template",
                "udm-metrics-collector.sh.template",
                "install-udm.sh.template"
            },
            AgentType.Linux => new List<string>
            {
                "linux-agent.sh.template",
                "linux-agent.service.template",
                "install-linux.sh.template"
            },
            _ => throw new ArgumentException($"Unknown agent type: {agentType}")
        };
    }

    /// <summary>
    /// Builds a Scriban script object with all configuration values
    /// </summary>
    private ScriptObject BuildScriptObject(AgentConfiguration config)
    {
        var scriptObject = new ScriptObject
        {
            { "agent_id", config.AgentId },
            { "device_name", config.DeviceName },
            { "agent_type", config.AgentType.ToString().ToLower() },
            { "influxdb_url", config.InfluxDbUrl },
            { "influxdb_org", config.InfluxDbOrg },
            { "influxdb_bucket", config.InfluxDbBucket },
            { "influxdb_token", config.InfluxDbToken },
            { "collection_interval", config.CollectionIntervalSeconds },
            { "speedtest_interval", config.SpeedtestIntervalMinutes },
            { "enable_docker", config.EnableDockerMetrics },
            { "is_udm", config.AgentType == AgentType.UDM },
            { "is_ucg", config.AgentType == AgentType.UCG },
            { "is_linux", config.AgentType == AgentType.Linux },
            { "is_unifi", config.AgentType == AgentType.UDM || config.AgentType == AgentType.UCG }
        };

        // Add tags as a dictionary
        if (config.Tags.Any())
        {
            var tagsObject = new ScriptObject();
            foreach (var tag in config.Tags)
            {
                tagsObject.Add(tag.Key, tag.Value);
            }
            scriptObject.Add("tags", tagsObject);
        }

        var context = new TemplateContext();
        context.PushGlobal(scriptObject);

        return scriptObject;
    }

    /// <summary>
    /// Validates that all required templates exist
    /// </summary>
    public bool ValidateTemplates(AgentType agentType, out List<string> missingTemplates)
    {
        missingTemplates = new List<string>();
        var templates = GetTemplatesForAgent(agentType);

        foreach (var template in templates)
        {
            var path = Path.Combine(_templatesPath, template);
            if (!File.Exists(path))
            {
                missingTemplates.Add(template);
            }
        }

        return missingTemplates.Count == 0;
    }

    /// <summary>
    /// Lists all available templates
    /// </summary>
    public List<string> ListAvailableTemplates()
    {
        if (!Directory.Exists(_templatesPath))
        {
            return new List<string>();
        }

        return Directory.GetFiles(_templatesPath, "*.template")
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToList();
    }
}
