using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Autonomous;

public static class AutonomousActivationService
{
    public static void Validate(AutonomousActivationPolicy? policy)
    {
        if (policy is null)
        {
            return;
        }

        var mode = NormalizeMode(policy.Mode);
        if (!string.Equals(mode, "always", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Autonomous activation mode must be either 'always' or 'prompt'.");
        }

        if (string.Equals(mode, "always", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (policy.Prompts is null || policy.Prompts.Count == 0)
        {
            throw new InvalidOperationException("Prompt-gated autonomous activation requires at least one prompt.");
        }

        var normalizedPrompts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prompt in policy.Prompts)
        {
            var normalized = Normalize(prompt);
            if (normalized.Length == 0)
            {
                throw new InvalidOperationException("Prompt-gated autonomous activation cannot contain an empty prompt.");
            }

            if (!normalizedPrompts.Add(normalized))
            {
                throw new InvalidOperationException("Prompt-gated autonomous activation cannot contain duplicate prompts after normalization.");
            }
        }
    }

    public static bool IsActivated(AutonomousActivationPolicy? policy, string? submittedPrompt)
    {
        if (policy is null || string.Equals(NormalizeMode(policy.Mode), "always", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var activationCandidate = ExtractActivationCandidate(submittedPrompt);
        if (activationCandidate is null)
        {
            return false;
        }

        var normalizedSubmittedPrompt = Normalize(activationCandidate);
        return normalizedSubmittedPrompt.Length > 0 &&
            (policy.Prompts ?? new List<string>())
                .Select(Normalize)
                .Any(prompt => string.Equals(prompt, normalizedSubmittedPrompt, StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        var result = builder.ToString();
        if (result.EndsWith(".", StringComparison.Ordinal))
        {
            result = result[..^1].TrimEnd();
        }

        return result;
    }

    private static string? ExtractActivationCandidate(string? submittedPrompt)
    {
        if (string.IsNullOrWhiteSpace(submittedPrompt))
        {
            return submittedPrompt;
        }

        var trimmed = submittedPrompt.TrimStart();
        if (!LooksLikeHeartbeatEnvelope(trimmed))
        {
            return submittedPrompt;
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };

            using var reader = XmlReader.Create(new StringReader(submittedPrompt), settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null || root.Name != "heartbeat")
            {
                return null;
            }

            var instructionElements = root.Descendants("instructions").ToList();
            if (instructionElements.Count != 1 ||
                instructionElements[0].Parent != root ||
                instructionElements[0].Nodes().Any(node => node is not XText))
            {
                return null;
            }

            return instructionElements[0].Value;
        }
        catch (XmlException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool LooksLikeHeartbeatEnvelope(string value)
    {
        const string heartbeat = "<heartbeat";
        if (!value.StartsWith(heartbeat, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Length == heartbeat.Length ||
            char.IsWhiteSpace(value[heartbeat.Length]) ||
            value[heartbeat.Length] is '>' or '/';
    }

    private static string NormalizeMode(string? mode) => mode?.Trim() ?? string.Empty;
}
