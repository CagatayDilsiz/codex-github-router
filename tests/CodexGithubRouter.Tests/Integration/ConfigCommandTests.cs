using System.Text.Json;
using System.Text.Json.Nodes;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class ConfigCommandTests
{
    [Fact]
    public async Task Config_path_shows_global_path()
    {
        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "path" }, deps);

        Assert.Equal(0, result);
        var lines = output.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Single(lines);
        Assert.Contains("workflow.json", lines[0]);
    }

    [Fact]
    public async Task Config_path_shows_repository_override_when_present()
    {
        using var sandbox = new TestSandbox();
        var repoRoot = sandbox.RepositoryDirectory;
        var repoConfigDir = Path.Combine(repoRoot, ".codex-github-router");
        Directory.CreateDirectory(repoConfigDir);
        var repoConfigPath = Path.Combine(repoConfigDir, "workflow.json");
        await File.WriteAllTextAsync(repoConfigPath, """{"version":1,"policies":{}}""");

        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            GetRepositoryRootAsync = _ => Task.FromResult<string?>(repoRoot)
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "path", repoRoot }, deps);

        Assert.Equal(0, result);
        var lines = output.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Contains("workflow.json", lines[0]);
        Assert.Contains(repoConfigPath, lines[1]);
    }

    [Fact]
    public async Task Config_show_displays_global_configuration()
    {
        using var sandbox = new TestSandbox();
        await WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile);

        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            LoadGlobalAsync = () => WorkflowConfigurationService.LoadOrDefaultAsync(sandbox.Paths)
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "show" }, deps);

        Assert.Equal(0, result);
        var json = output.ToString().TrimEnd();
        var document = JsonNode.Parse(json);
        Assert.NotNull(document);
    }

    [Fact]
    public async Task Config_show_uses_defaults_when_global_file_missing()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            LoadGlobalAsync = () => WorkflowConfigurationService.LoadOrDefaultAsync(sandbox.Paths)
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "show" }, deps);

        Assert.Equal(0, result);
        var json = output.ToString().TrimEnd();
        var config = JsonSerializer.Deserialize<RouterConfiguration>(json, WorkflowJson.Options);
        Assert.NotNull(config);
        Assert.Equal(1, config.Version);
    }

    [Fact]
    public async Task Config_show_effective_displays_effective_configuration()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            LoadEffectiveAsync = _ => Task.FromResult(new RouterConfiguration())
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "show", "--effective", sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        var json = output.ToString().TrimEnd();
        var document = JsonNode.Parse(json);
        Assert.NotNull(document);
    }

    [Fact]
    public async Task Config_show_effective_fails_without_git_repo()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            LoadEffectiveAsync = _ => throw new InvalidOperationException("Not a valid Git repository.")
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "show", "--effective", sandbox.Root }, deps);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Config_validate_returns_zero_for_valid_configuration()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            LoadEffectiveAsync = _ => Task.FromResult(new RouterConfiguration())
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "validate", sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        Assert.Contains("Configuration is valid.", output.ToString());
    }

    [Fact]
    public async Task Config_validate_returns_nonzero_for_invalid_configuration()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            LoadEffectiveAsync = _ => throw new InvalidOperationException("Unsupported workflow configuration version: 99")
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "validate", sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        Assert.Contains("Configuration is invalid:", output.ToString());
    }

    [Fact]
    public async Task Config_path_uses_current_directory_when_no_arg()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "cgr-config-current-dir");
        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            GetRepositoryRootAsync = _ => Task.FromResult<string?>(repoRoot),
            FileExists = path => path.Contains(".codex-github-router")
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "path" }, deps);

        Assert.Equal(0, result);
        var lines = output.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        var expectedRepoPath = Path.Combine(repoRoot, ".codex-github-router", "workflow.json");
        Assert.Equal(expectedRepoPath, lines[1]);
    }

    [Fact]
    public async Task Config_path_too_many_args_returns_error()
    {
        var deps = new ConfigCommandDependencies();
        var result = await ConfigCommandHandler.HandleAsync(new[] { "path", "dir1", "dir2" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Config_show_with_typo_option_returns_error()
    {
        var deps = new ConfigCommandDependencies();
        var result = await ConfigCommandHandler.HandleAsync(new[] { "show", "--efective" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Config_show_with_extra_args_returns_error()
    {
        var deps = new ConfigCommandDependencies();
        var result = await ConfigCommandHandler.HandleAsync(new[] { "show", "extra" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Config_show_effective_with_too_many_args_returns_error()
    {
        var deps = new ConfigCommandDependencies();
        var result = await ConfigCommandHandler.HandleAsync(new[] { "show", "--effective", "dir1", "dir2" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Config_validate_too_many_args_returns_error()
    {
        var deps = new ConfigCommandDependencies();
        var result = await ConfigCommandHandler.HandleAsync(new[] { "validate", "dir1", "dir2" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Config_path_with_unknown_flag_returns_error()
    {
        var deps = new ConfigCommandDependencies();
        var result = await ConfigCommandHandler.HandleAsync(new[] { "path", "--unknown" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Config_validate_with_unknown_flag_returns_error()
    {
        var deps = new ConfigCommandDependencies();
        var result = await ConfigCommandHandler.HandleAsync(new[] { "validate", "--unknown" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Config_show_effective_with_unknown_flag_returns_error()
    {
        var deps = new ConfigCommandDependencies();
        var result = await ConfigCommandHandler.HandleAsync(new[] { "show", "--effective", "--unknown" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Config_show_effective_with_repository_override()
    {
        using var sandbox = new TestSandbox();
        await WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile);

        var repoRoot = sandbox.RepositoryDirectory;
        await GitInitAsync(repoRoot);
        var repoConfigDir = Path.Combine(repoRoot, ".codex-github-router");
        Directory.CreateDirectory(repoConfigDir);
        var repoConfigPath = Path.Combine(repoConfigDir, "workflow.json");
        await File.WriteAllTextAsync(repoConfigPath, """{"version":1,"states":{"ready":[{"type":"label","values":["custom"]}]}}""");

        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            LoadEffectiveAsync = workingDirectory => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory, sandbox.Paths)
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "show", "--effective", repoRoot }, deps);

        Assert.Equal(0, result);
        var json = output.ToString().TrimEnd();
        var document = JsonNode.Parse(json)!.AsObject();
        Assert.NotNull(document);
        var readyStates = document["states"]!["ready"]!.AsArray();
        Assert.Single(readyStates);
        Assert.Equal("custom", readyStates[0]!["values"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Config_validate_with_repository_override_succeeds()
    {
        using var sandbox = new TestSandbox();
        await WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile);

        var repoRoot = sandbox.RepositoryDirectory;
        await GitInitAsync(repoRoot);
        var repoConfigDir = Path.Combine(repoRoot, ".codex-github-router");
        Directory.CreateDirectory(repoConfigDir);
        var repoConfigPath = Path.Combine(repoConfigDir, "workflow.json");
        await File.WriteAllTextAsync(repoConfigPath, """{"version":1,"policies":{}}""");

        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output,
            LoadEffectiveAsync = workingDirectory => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory, sandbox.Paths)
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "validate", repoRoot }, deps);

        Assert.Equal(0, result);
        Assert.Contains("Configuration is valid.", output.ToString());
    }

    [Fact]
    public async Task Config_unknown_subcommand_returns_error()
    {
        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output
        };

        var result = await ConfigCommandHandler.HandleAsync(new[] { "unknown" }, deps);

        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Config_no_subcommand_returns_error()
    {
        var output = new StringWriter();
        var deps = new ConfigCommandDependencies
        {
            Output = output
        };

        var result = await ConfigCommandHandler.HandleAsync(Array.Empty<string>(), deps);

        Assert.Equal(2, result);
    }

    private static async Task GitInitAsync(string path)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "init",
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        await process.WaitForExitAsync();
    }
}
