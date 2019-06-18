//#module nuget:?package=Cake.DotNetTool.Module&version=0.3.0
//#tool "dotnet:?package=GitVersion.Tool&version=5.0.0-beta2-6"
//#tool "dotnet:?package=GitVersion.Tool&version=4.0.1-beta1-65"
#tool "nuget:?package=GitVersion.CommandLine&version=4.0.0-beta0012";
#tool "nuget:?package=OctopusTools&version=6.7.0"
#addin "nuget:?package=Cake.Npm&version=0.17.0"
#addin "nuget:?package=Cake.Curl&version=4.1.0"

#load "build/paths.cake"
#load "build/version.cake"
#load "build/package.cake"
#load "build/urls.cake"

var target = Argument("Target", "Build-CI");

Setup<PackageMetadata>(context =>
{
    return new PackageMetadata(
        outputDirectory: Argument("packageOutputDirectory", "packages"),
        name: "Linker-4");
});

Task("Compile")
    .Does(() =>
{
    DotNetCoreBuild(Paths.SolutionFile.FullPath);

});

Task("Test")
    .IsDependentOn("Compile")
    .Does(() =>
{
    DotNetCoreTest(Paths.TestProjectFile.FullPath);
});

Task("Version")
    .Does<PackageMetadata>(package =>
{
    package.Version = ReadVersionFromProjectFile(Context);

    if (package.Version != null)
    {
        Information($"Read version {package.Version} from the project file");
    }
    else
    {
        package.Version = GitVersion().FullSemVer;
        Information($"Calculated version {package.Version} from the Git history");
    }
});

Task("Build-Frontend")
    .Does(() =>
{
    NpmInstall(settings => settings.FromPath(Paths.FrontendDirectory));

    NpmRunScript("build", settings => settings.FromPath(Paths.FrontendDirectory));
});

Task("Package-Zip")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package =>
{
    CleanDirectory(package.OutputDirectory);

    package.Extension = "zip";
    DotNetCorePublish(
        Paths.WebProjectFile.GetDirectory().FullPath,
        new DotNetCorePublishSettings{
            OutputDirectory = Paths.PublishDirectory,
            NoBuild = true,
            NoRestore = true,
            MSBuildSettings = new DotNetCoreMSBuildSettings
            {
                NoLogo = true
            }
        }
    );
    Zip(Paths.PublishDirectory, package.FullPath);
});

Task("Package-Octopus")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package =>
{
    CleanDirectory(package.OutputDirectory);

    package.Extension = "nupkg";
    DotNetCorePublish(
        Paths.WebProjectFile.GetDirectory().FullPath,
        new DotNetCorePublishSettings{
            OutputDirectory = Paths.PublishDirectory,
            NoBuild = true,
            NoRestore = true,
            MSBuildSettings = new DotNetCoreMSBuildSettings
            {
                NoLogo = true
            }
        }
    );
    OctoPack(package.Name, new OctopusPackSettings{
        Format = OctopusPackFormat.NuPkg,
        Version = package.Version,
        BasePath = Paths.PublishDirectory,
        OutFolder = package.OutputDirectory
    });
});

Task("Deploy-Kudu")
    .Description("Deploys to Kudu using the Zip deployment feature")
    .IsDependentOn("Package-Zip")
    .Does<PackageMetadata>(package =>
    {
        CurlUploadFile(
            package.FullPath,
            Urls.KuduDeployUrl,
            new CurlSettings
            {
                Username = EnvironmentVariable("DeploymentUser"),
                Password = EnvironmentVariable("DeploymentPassword"),
                RequestCommand = "POST",
                ProgressBar = true,
                Fail = true
                //ArgumentCustomization = args => args.Append("--fail")
            });
    });

Task("Deploy-Octopus")
    .IsDependentOn("Package-Octopus")
    .Does<PackageMetadata>(package =>
    {
        OctoPush(
            Urls.OctopusServerUrl.AbsoluteUri,
            EnvironmentVariable("OctopusApiKey"),
            package.FullPath,
            new OctopusPushSettings
            {
                EnableServiceMessages = true,
                ReplaceExisting = true
            });

        OctoCreateRelease(
            "Linker-4",
            new CreateReleaseSettings
            {
                Server = "http://octopus-megakemp.northeurope.cloudapp.azure.com",
                ApiKey = EnvironmentVariable("OctopusApiKey"),
                ReleaseNumber = package.Version,
                DefaultPackageVersion = package.Version,
                DeployTo = "Test",
                IgnoreExisting = true,
                DeploymentProgress = true,
                WaitForDeployment = true
            });
    });

Task("Set-Build-Number")
    .WithCriteria(() => BuildSystem.IsRunningOnTeamCity)
    .Does<PackageMetadata>(package =>
    {
        var buildNumber = TeamCity.Environment.Build.Number;
        //TFBuild.Commands.UpdateBuildNumber(package.Version);
        TeamCity.SetBuildNumber($"{package.Version}+{buildNumber}");
    });

Task("Publish-Build-Artifact")
    .WithCriteria(() => BuildSystem.IsRunningOnTeamCity)
    .IsDependentOn("Package-Zip")
    .Does<PackageMetadata>(package =>
    {
        //TFBuild.Commands.UploadArtifactDirectory(Package.OutputDirectory);
        foreach (var p in GetFiles(package.OutputDirectory + $"/{package.Extension}"))
        {
            TeamCity.PublishArtifacts(p.FullPath);
        }
    });

Task("Build-CI")
    .IsDependentOn("Compile")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .IsDependentOn("Package-Zip")
    .IsDependentOn("Set-Build-Number")
    .IsDependentOn("Publish-Build-Artifact");

RunTarget(target);