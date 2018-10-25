using Cake.Common.Diagnostics;
using Cake.Common.Solution;
using Cake.Core;
using CK.Text;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeCake
{
    public partial class Build
    {
        static class NuGetHelper
        {
            static SourceCacheContext _sourceCache;
            static List<Lazy<INuGetResourceProvider>> _providers;
            static ILogger _logger;
            static ISettings _settings;
            static IPackageSourceProvider _sourceProvider;

            static NuGetHelper()
            {
                _sourceCache = new SourceCacheContext();
                _providers = new List<Lazy<INuGetResourceProvider>>();
                _providers.AddRange( Repository.Provider.GetCoreV3() );
            }

            public static void SetupCredentialService( IPackageSourceProvider sourceProvider, ILogger logger, bool nonInteractive )
            {
                var providers = new AsyncLazy<IEnumerable<ICredentialProvider>>( async () => await GetCredentialProvidersAsync( sourceProvider, logger ) );
                HttpHandlerResourceV3.CredentialService = new Lazy<ICredentialService>(
                    () => new CredentialService(
                                providers: providers,
                                nonInteractive: nonInteractive,
                                handlesDefaultCredentials: true ) );

            }

            #region Credential provider for Credential section of nuget.config.
            // Must be upgraded when a 4.9 or 5.0 is out.
            // This currently only support "basic" authentication type.
            public class SettingsCredentialProvider : ICredentialProvider
            {
                private readonly IPackageSourceProvider _packageSourceProvider;

                public SettingsCredentialProvider( IPackageSourceProvider packageSourceProvider )
                {
                    if( packageSourceProvider == null )
                    {
                        throw new ArgumentNullException( nameof( packageSourceProvider ) );
                    }
                    _packageSourceProvider = packageSourceProvider;
                    Id = $"{typeof( SettingsCredentialProvider ).Name}_{Guid.NewGuid()}";
                }

                /// <summary>
                /// Unique identifier of this credential provider
                /// </summary>
                public string Id { get; }


                public Task<CredentialResponse> GetAsync(
                    Uri uri,
                    IWebProxy proxy,
                    CredentialRequestType type,
                    string message,
                    bool isRetry,
                    bool nonInteractive,
                    CancellationToken cancellationToken )
                {
                    if( uri == null ) throw new ArgumentNullException( nameof( uri ) );

                    cancellationToken.ThrowIfCancellationRequested();

                    ICredentials cred = null;

                    // If we are retrying, the stored credentials must be invalid.
                    if( !isRetry && type != CredentialRequestType.Proxy )
                    {
                        cred = GetCredentials( uri );
                    }

                    var response = cred != null
                        ? new CredentialResponse( cred )
                        : new CredentialResponse( CredentialStatus.ProviderNotApplicable );

                    return System.Threading.Tasks.Task.FromResult( response );
                }

                private ICredentials GetCredentials( Uri uri )
                {
                    var source = _packageSourceProvider.LoadPackageSources().FirstOrDefault( p =>
                    {
                        Uri sourceUri;
                        return p.Credentials != null
                            && p.Credentials.IsValid()
                            && Uri.TryCreate( p.Source, UriKind.Absolute, out sourceUri )
                            && UriEquals( sourceUri, uri );
                    } );
                    if( source == null )
                    {
                        // The source is not in the config file
                        return null;
                    }
                    // In 4.8.0 version, there is not yet the ValidAuthenticationTypes nor the ToICredentials() method.
                    // return source.Credentials.ToICredentials();
                    return new AuthTypeFilteredCredentials( new NetworkCredential( source.Credentials.Username, source.Credentials.Password ), new[] { "basic" } );
                }

                /// <summary>
                /// Determines if the scheme, server and path of two Uris are identical.
                /// </summary>
                private static bool UriEquals( Uri uri1, Uri uri2 )
                {
                    uri1 = CreateODataAgnosticUri( uri1.OriginalString.TrimEnd( '/' ) );
                    uri2 = CreateODataAgnosticUri( uri2.OriginalString.TrimEnd( '/' ) );

                    return Uri.Compare( uri1, uri2, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase ) == 0;
                }

                // Bug 2379: SettingsCredentialProvider does not work
                private static Uri CreateODataAgnosticUri( string uri )
                {
                    if( uri.EndsWith( "$metadata", StringComparison.OrdinalIgnoreCase ) )
                    {
                        uri = uri.Substring( 0, uri.Length - 9 ).TrimEnd( '/' );
                    }
                    return new Uri( uri );
                }
            }
            #endregion

            class Logger : NuGet.Common.ILogger
            {
                readonly ICakeContext _ctx;
                readonly object _lock;

                public Logger( ICakeContext ctx )
                {
                    _ctx = ctx;
                    _lock = new object();
                }

                public void LogDebug( string data ) { lock( _lock ) _ctx.Debug( $"NuGet: {data}" ); }
                public void LogVerbose( string data ) { lock( _lock ) _ctx.Verbose( $"NuGet: {data}" ); }
                public void LogInformation( string data ) { lock( _lock ) _ctx.Information( $"NuGet: {data}" ); }
                public void LogMinimal( string data ) { lock( _lock ) _ctx.Information( $"NuGet: {data}" ); }
                public void LogWarning( string data ) { lock( _lock ) _ctx.Warning( $"NuGet: {data}" ); }
                public void LogError( string data ) { lock( _lock ) _ctx.Error( $"NuGet: {data}" ); }
                public void LogSummary( string data ) { lock( _lock ) _ctx.Information( $"NuGet: {data}" ); }
                public void LogInformationSummary( string data ) { lock( _lock ) _ctx.Information( $"NuGet: {data}" ); }
                public void Log( NuGet.Common.LogLevel level, string data ) { lock( _lock ) _ctx.Information( $"NuGet ({level}): {data}" ); }
                public Task LogAsync( NuGet.Common.LogLevel level, string data )
                {
                    Log( level, data );
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                public void Log( NuGet.Common.ILogMessage message )
                {
                    lock( _lock ) _ctx.Information( $"NuGet ({message.Level}) - Code: {message.Code} - Project: {message.ProjectPath} - {message.Message}" );
                }

                public Task LogAsync( NuGet.Common.ILogMessage message )
                {
                    Log( message );
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            }

            static NuGet.Common.ILogger Initialize( ICakeContext ctx )
            {
                if( _logger == null )
                {
                    _logger = new Logger( ctx );
                    _settings = Settings.LoadDefaultSettings( Environment.CurrentDirectory );
                    _sourceProvider = new PackageSourceProvider( _settings );
                    var credProviders = new AsyncLazy<IEnumerable<ICredentialProvider>>( async () => await GetCredentialProvidersAsync( _sourceProvider, _logger ) );
                    HttpHandlerResourceV3.CredentialService = new Lazy<ICredentialService>(
                        () => new CredentialService(
                            providers: credProviders,
                            nonInteractive: true,
                            handlesDefaultCredentials: true ) );
                }
                return _logger;
            }
            static async Task<IEnumerable<ICredentialProvider>> GetCredentialProvidersAsync( IPackageSourceProvider sourceProvider, ILogger logger )
            {
                var providers = new List<ICredentialProvider>();

                var securePluginProviders = await new SecurePluginCredentialProviderBuilder( pluginManager: PluginManager.Instance, canShowDialog: false, logger: logger ).BuildAllAsync();
                providers.AddRange( securePluginProviders );
                providers.Add( new SettingsCredentialProvider( sourceProvider ) );
                return providers;
            }

            public class Feed
            {
                readonly PackageSource _packageSource;
                readonly SourceRepository _sourceRepository;
                List<SolutionProject> _packagesToPublish;

                public Feed( string name, string urlV3 )
                {
                    Name = name;
                    _packageSource = new PackageSource( urlV3 );
                    _sourceRepository = new SourceRepository( _packageSource, _providers );
                }

                public string Url => _packageSource.Source;

                public string Name { get; }

                public IReadOnlyList<SolutionProject> PackagesToPublish => _packagesToPublish;

                public int PackagesAlreadyPublishedCount { get; private set; }

                public async Task InitializePackagesToPublishAsync( ICakeContext ctx, IEnumerable<SolutionProject> projectsToPublish, string nuGetVersion )
                {
                    if( _packagesToPublish == null )
                    {
                        _packagesToPublish = new List<SolutionProject>();
                        var targetVersion = NuGetVersion.Parse( nuGetVersion );
                        MetadataResource meta = await _sourceRepository.GetResourceAsync<MetadataResource>();
                        foreach( var p in projectsToPublish )
                        {
                            var id = new PackageIdentity( p.Name, targetVersion );
                            if( await meta.Exists( id, _sourceCache, Initialize( ctx ), CancellationToken.None ) )
                            {
                                ++PackagesAlreadyPublishedCount;
                            }
                            else
                            {
                                ctx.Debug( $"Package {p.Name} must be published to remote feed '{Name}'." );
                                _packagesToPublish.Add( p );
                            }
                        }
                    }
                    ctx.Debug( $" ==> {_packagesToPublish.Count} package(s) must be published to remote feed '{Name}'." );
                }

                public void Information( ICakeContext ctx, IEnumerable<SolutionProject> projectsToPublish )
                {
                    if( PackagesToPublish.Count == 0 )
                    {
                        ctx.Information( $"Feed '{Name}': No packages must be pushed ({PackagesAlreadyPublishedCount} packages already available)." );
                    }
                    else if( PackagesAlreadyPublishedCount == 0 )
                    {
                        ctx.Information( $"Feed '{Name}': All {PackagesAlreadyPublishedCount} packages must be pushed." );
                    }
                    else
                    {
                        ctx.Information( $"Feed '{Name}': {PackagesToPublish.Count} packages must be pushed: {PackagesToPublish.Select( p => p.Name ).Concatenate()}." );
                        ctx.Information( $"               => {PackagesAlreadyPublishedCount} packages already pushed: {projectsToPublish.Except( PackagesToPublish ).Select( p => p.Name ).Concatenate()}." );
                    }
                }
            }
        }

        class SignatureOpenSourcePublicFeed : NuGetHelper.Feed
        {
            public SignatureOpenSourcePublicFeed( string feedName )
                : base( feedName, $"https://pkgs.dev.azure.com/Signature-OpenSource/_packaging/{feedName}/nuget/v3/index.json" )
            {
            }
        }

    }
}

