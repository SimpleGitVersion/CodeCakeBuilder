using Cake.Common;
using Cake.Common.Solution;
using Cake.Common.IO;
using Cake.Common.Tools.MSBuild;
using Cake.Common.Tools.NuGet;
using Cake.Core;
using Cake.Common.Diagnostics;
using Code.Cake;
using Cake.Common.Tools.NuGet.Pack;
using System.Linq;
using Cake.Core.Diagnostics;
using Cake.Common.Tools.NuGet.Restore;
using System;
using Cake.Common.Tools.NuGet.Push;

namespace CodeCake
{
    /// <summary>
    /// Sample build "script".
    /// Build scripts can be decorated with AddPath attributes that inject existing paths into the PATH environment variable. 
    /// </summary>
    [AddPath( "%LOCALAPPDATA%/My-Marvelous-Tools" )]
    [AddPath( "$safeprojectname$/Tools" )]
    [AddPath( "packages/**/tools*" )]
    public class Build : CodeCakeHost
    {
        public Build()
        {
            // The configuration ("Debug", etc.) defaults to "Release".
            var configuration = Cake.Argument( "configuration", "Release" );

            // Git .ignore file should ignore this folder.
            // Here, we name it "Releases" (default , it could be "Artefacts", "Publish" or anything else, 
            // but "Releases" is by default ignored in https://github.com/github/gitignore/blob/master/VisualStudio.gitignore.
            var releasesDir = Cake.Directory( "$safeprojectname$/Releases" );

            Task( "Clean" )
                .Does( () =>
                {
                    // Avoids cleaning $safeprojectname$ itself!
                    Cake.CleanDirectories( "**/bin/" + configuration, d => !d.Path.Segments.Contains( "$safeprojectname$" ) );
                    Cake.CleanDirectories( "**/obj/" + configuration, d => !d.Path.Segments.Contains( "$safeprojectname$" ) );
                    Cake.CleanDirectories( releasesDir );
                } );

            Task( "Restore-NuGet-Packages" )
                .Does( () =>
                {
                    // Reminder for first run.
                    // Bootstrap.ps1 ensures that Tools/nuget.exe exists
                    // and compiles this $safeprojectname$ application in Release mode.
                    // It is the first thing that a CI should execute in the initialization phase and
                    // once done bin/Release/$safeprojectname$.exe can be called to do its job.
                    // (Of course, the following check can be removed and nuget.exe be conventionnaly located somewhere else.)
                    if( !Cake.FileExists( "$safeprojectname$/Tools/nuget.exe" ) )
                    {
                        throw new Exception( "Please execute Bootstrap.ps1 first." );
                    }

                    Cake.Information( "Restoring nuget packages for existing .sln files at the root level.", configuration );
                    foreach( var sln in Cake.GetFiles( "*.sln" ) )
                    {
                        Cake.NuGetRestore( sln );
                    }
                } );

            Task( "Build" )
                .IsDependentOn( "Clean" )
                .IsDependentOn( "Restore-NuGet-Packages" )
                .Does( () =>
                {
                    Cake.Information( "Building all existing .sln files at the root level with '{0}' configuration (excluding this builder application).", configuration );
                    foreach( var sln in Cake.GetFiles( "*.sln" ) )
                    {
                        using( var tempSln = Cake.CreateTemporarySolutionFile( sln ) )
                        {
                            // Excludes $safeprojectname$itself from compilation!
                            tempSln.ExcludeProjectsFromBuild( "$safeprojectname$" );
                            Cake.MSBuild( tempSln.FullPath, new MSBuildSettings()
                                    .SetConfiguration( configuration )
                                    .SetVerbosity( Verbosity.Minimal )
                                    .SetMaxCpuCount( 1 ) );
                        }
                    }
                } );

            Task( "Create-NuGet-Packages" )
                .IsDependentOn( "Build" )
                .Does( () =>
                {
                    Cake.CreateDirectory( releasesDir );
                    var settings = new NuGetPackSettings()
                    {
                        // Hard coded version!?
                        // Cake offers tools to extract the version number from a ReleaseNotes.txt.
                        // But other tools exist: have a look at SimpleGitVersion.Cake to easily 
                        // manage Constrained Semantic Versions on Git repositories.
                        Version = "1.0.0-alpha",
                        BasePath = Cake.Environment.WorkingDirectory,
                        OutputDirectory = releasesDir
                    };
                    foreach( var nuspec in Cake.GetFiles( "$safeprojectname$/NuSpec/*.nuspec" ) )
                    {
                        Cake.NuGetPack( nuspec, settings );
                    }

                } );

            // We want to push on NuGet only the Release packages.
            Task( "Push-NuGet-Packages" )
                .IsDependentOn( "Create-NuGet-Packages" )
                .WithCriteria( () => configuration == "Release" )
                .Does( () =>
                {
                    // Resolve the API key: if the environment variable is not found
                    // AND $safeprojectname$ is running in interactive mode (ie. no -nointeraction parameter),
                    // then the user is prompted to enter it.
                    // This is specific to CodeCake (in Code.Cake.dll).
                    var apiKey = Cake.InteractiveEnvironmentVariable( "NUGET_API_KEY" );
                    if( string.IsNullOrEmpty( apiKey ) ) throw new InvalidOperationException( "Could not resolve NuGet API key." );

                    var settings = new NuGetPushSettings
                    {
                        Source = "https://www.nuget.org/api/v2/package",
                        ApiKey = apiKey
                    };

                    foreach( var nupkg in Cake.GetFiles( releasesDir.Path + "/*.nupkg" ) )
                    {
                        Cake.NuGetPush( nupkg, settings );
                    }
                } );

            Task( "Default" ).IsDependentOn( "Push-NuGet-Packages" );

        }
    }
}
