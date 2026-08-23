using Algorand;
using Algorand.Algod;
using AlgorandAuthenticationV2;
using AVMIndexReporter.Repository;
using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Processors.Image;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Security;
using Elastic.Transport;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using NLog.Web;
using StackExchange.Redis;
using System.Text.Json.Serialization; // added for ReferenceHandler (kept in case future customization)
using System.Threading.RateLimiting;

namespace AVMTradeReporter
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Remove the default Console/Debug/EventSource logging providers so only NLog's
            // JSON console target writes to stdout - otherwise every log line is emitted twice,
            // once as plain "info: Category[0]\n      Message" text and once as JSON.
            builder.Logging.ClearProviders();
            builder.UseNLog();

            // Add services to the container.
            builder.Services.AddControllers();

            // Response compression (gzip/brotli) - large JSON list responses compress extremely well
            builder.Services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
                options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
            });
            builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(options =>
            {
                options.Level = System.IO.Compression.CompressionLevel.Fastest;
            });
            builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(options =>
            {
                options.Level = System.IO.Compression.CompressionLevel.Fastest;
            });
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "AVM Trade Reporter API",
                    Version = "v1",
                    Description = File.ReadAllText("doc/description.md"),
                });
                c.AddSecurityDefinition("arc14", new OpenApiSecurityScheme
                {
                    Description = "ARC-0014 Algorand authentication transaction, base64-encoded and sent as a " +
                        "Bearer token in the Authorization header (\"Authorization: Bearer <token>\"). See the " +
                        "top-level API description for how to generate this token.",
                    In = ParameterLocation.Header,
                    Name = "Authorization",
                    Type = SecuritySchemeType.ApiKey,
                });
                c.OperationFilter<Swashbuckle.AspNetCore.Filters.SecurityRequirementsOperationFilter>();
                c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First()); //This line

                // Surface /// XML doc comments (summary/param/returns) from this assembly in Swagger UI.
                // Requires <GenerateDocumentationFile> in the csproj.
                var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                if (File.Exists(xmlPath))
                {
                    c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
                }
            });

            // Configure AppConfiguration from appsettings.json
            builder.Services.Configure<AppConfiguration>(
                builder.Configuration.GetSection("AppConfiguration"));

            // Add Redis
            var appConfig = builder.Configuration.GetSection("AppConfiguration").Get<AppConfiguration>();
            if (appConfig?.Redis?.Enabled == true)
            {
                builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
                {
                    var configuration = appConfig.Redis.ConnectionString;
                    return ConnectionMultiplexer.Connect(configuration);
                });

                builder.Services.AddSingleton<IDatabase>(sp =>
                {
                    var connectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
                    return connectionMultiplexer.GetDatabase(appConfig.Redis.DatabaseId);
                });

                builder.Services.AddHealthChecks().AddCheck<HealthChecks.RedisHealthCheck>("redis");
            }

            // Add Algorand API client
            builder.Services.AddSingleton<IDefaultApi>(sp =>
            {
                var config = sp.GetRequiredService<IOptions<AppConfiguration>>().Value;
                var httpClient = HttpClientConfigurator.ConfigureHttpClient(
                    config.Algod.Host,
                    config.Algod.ApiKey,
                    config.Algod.Header);
                return new DefaultApi(httpClient);
            });
            builder.Services.AddSingleton<BlockRepository>();
            builder.Services.AddSingleton<IAssetRepository, AssetRepository>();
            builder.Services.AddSingleton<MainnetImageProcessor>();
            builder.Services.AddSingleton<IndexerRepository>();
            builder.Services.AddSingleton<IPoolRepository, PoolRepository>();
            builder.Services.AddSingleton<PoolRepository>();
            builder.Services.AddSingleton<AggregatedPoolRepository>();
            builder.Services.AddSingleton<TradeRepository>();
            builder.Services.AddSingleton<LiquidityRepository>();
            builder.Services.AddSingleton<TransactionProcessor>();
            builder.Services.AddSingleton<OHLCRepository>(); // register OHLC repository
            builder.Services.AddSingleton<TvlOhlcRepository>(); // register TVL OHLC repository
            builder.Services.AddSingleton<ISearchService, SearchService>();
            builder.Services.AddSingleton<ITradeQueryService, TradeQueryService>();
            builder.Services.AddSingleton<ILiquidityQueryService, LiquidityQueryService>();
            builder.Services.AddSingleton<IOHLCService, OHLCService>();
            builder.Services.AddSingleton<IStatsRepository, StatsRepository>();
            builder.Services.AddSingleton<IStatsService, StatsService>();
            builder.Services.AddSingleton<IAssetStatRepository, AssetStatRepository>();
            builder.Services.AddSingleton<IAssetStatsService, AssetStatsService>();
            builder.Services.AddSingleton<ITopAssetsService, TopAssetsService>();
            builder.Services.AddSingleton<IAssetTimeseriesService, AssetTimeseriesService>();
            builder.Services.AddSingleton<IOhlcUsdRepairService, OhlcUsdRepairService>();

            // Add Pool Processors
            builder.Services.AddSingleton<PactPoolProcessor>();
            builder.Services.AddSingleton<TinyPoolProcessor>();
            builder.Services.AddSingleton<BiatecPoolProcessor>();

            // Register the background services
            builder.Services.AddHostedService<TradeReporterBackgroundService>();
            // Registered as a singleton (not just AddHostedService) so GossipController can resolve the same
            // running instance to report live relay connection status.
            builder.Services.AddSingleton<GossipBackgroundService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<GossipBackgroundService>());

            // Register Pool Refresh Background Service only if enabled
            if (appConfig?.PoolRefresh?.Enabled == true)
            {
                builder.Services.AddHostedService<PoolRefreshBackgroundService>();
            }

            // Register Volume Update Background Service only if enabled
            if (appConfig?.VolumeUpdate?.Enabled != false) // default enabled
            {
                builder.Services.AddHostedService<VolumeUpdateBackgroundService>();
            }

            // Register Asset Stats Background Service only if enabled
            if (appConfig?.AssetStats?.Enabled != false) // default enabled
            {
                builder.Services.AddHostedService<AssetStatsBackgroundService>();
            }

            // Register Top Assets Background Service only if enabled
            if (appConfig?.TopAssets?.Enabled != false) // default enabled
            {
                builder.Services.AddHostedService<TopAssetsBackgroundService>();
            }

            // Register Asset Timeseries Background Service only if enabled
            if (appConfig?.AssetTimeseries?.Enabled != false) // default enabled
            {
                builder.Services.AddHostedService<AssetTimeseriesBackgroundService>();
            }

            // One-shot historical USD OHLC / TVL snapshot repair (idempotent, Redis-marker guarded)
            if (appConfig?.OhlcRepair?.Enabled != false) // default enabled
            {
                builder.Services.AddHostedService<OhlcUsdRepairBackgroundService>();
            }

            builder.Services.AddSingleton<ElasticsearchClient>(sp =>
            {
                var appConfig = sp.GetRequiredService<IOptions<AppConfiguration>>().Value;
                if (string.IsNullOrEmpty(appConfig.Elastic.Host) || string.IsNullOrEmpty(appConfig.Elastic.ApiKey)) return null!;// No Elasticsearch configured 
                var settings = new ElasticsearchClientSettings(new Uri(appConfig.Elastic.Host))
                .Authentication(new Elastic.Transport.ApiKey(appConfig.Elastic.ApiKey ?? throw new Exception("Api key for elastic is null")))
                .ThrowExceptions()
                .EnableDebugMode()
                .EnableHttpCompression()
                .PrettyJson()
                .RequestTimeout(TimeSpan.FromMinutes(1))
                .DefaultMappingFor<Models.Data.Trade>(m => m
                    .IndexName("trades")
                    .IdProperty(t => t.TxId))
                .DefaultMappingFor<Models.Data.Liquidity>(m => m
                    .IndexName("liquidity")
                    .IdProperty(t => t.TxId))
                .DefaultMappingFor<Models.Data.AggregatedPool>(m => m
                    .IndexName("liquidity")
                    .IdProperty(t => t.Id))
                .DefaultMappingFor<Models.Data.Pool>(m => m
                    .IndexName("pools")
                    .IdProperty(t => t.PoolAddress))
                .DefaultMappingFor<Models.Data.Indexer>(m => m
                    .IndexName("indexers")
                    .IdProperty(t => t.Id))
                .DefaultMappingFor<Models.Data.OHLC>(m => m
                    .IndexName("ohlc")
                    .IdProperty(t => t.Id))
                .DefaultMappingFor<Models.Data.AssetStat>(m => m
                    .IndexName("assetstats")
                    .IdProperty(t => t.Id))
                .DefaultMappingFor<Models.Data.AssetSnapshot>(m => m
                    .IndexName("assets")
                    .IdProperty(t => t.Id))
                ;

                return new ElasticsearchClient(settings);
            });

            // Add CORS policy - must be before SignalR and authentication
            var corsConfig = builder.Configuration.GetSection("Cors").AsEnumerable().Select(k => k.Value ?? "").Where(k => !string.IsNullOrEmpty(k)).ToArray();
            if (!(corsConfig?.Length > 0)) throw new Exception("Cors not defined");
            Console.WriteLine($"Cors: {string.Join(",", corsConfig)}");
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(
                builder =>
                {
                    builder.WithOrigins(corsConfig)
                        .SetIsOriginAllowedToAllowWildcardSubdomains()
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials();
                });
            });

            // Configure authentication
            var authOptions = builder.Configuration.GetSection("AlgorandAuthentication").Get<AlgorandAuthenticationOptionsV2>();
            if (authOptions == null) throw new Exception("Config for the authentication is missing");

            Console.WriteLine($"Auth Config: Realm={authOptions.Realm}, AllowEmptyAccounts={authOptions.AllowEmptyAccounts}, Debug={authOptions.Debug}");

            builder.Services.AddAuthentication(AlgorandAuthenticationHandlerV2.ID).AddAlgorand(a =>
            {
                a.Realm = authOptions.Realm;
                a.AllowEmptyAccounts = authOptions.AllowEmptyAccounts;
                a.CheckExpiration = authOptions.CheckExpiration;
                a.EmptySuccessOnFailure = authOptions.EmptySuccessOnFailure;
                a.AllowedNetworks = authOptions.AllowedNetworks;
                a.Debug = authOptions.Debug;
            });

            builder.Services.AddAuthorization();

            // Add SignalR after CORS and authentication
            builder.Services.AddSignalR(options =>
            {
                // Configure SignalR options for better debugging
                options.EnableDetailedErrors = builder.Environment.IsDevelopment();
                options.MaximumReceiveMessageSize = null; // Remove message size limit
            });

            builder.Services.AddProblemDetails();

            // Two tiers, both fixed 1-minute windows: 60 req/min for unauthenticated (anonymous or
            // failed ARC-14) traffic partitioned by client IP, 300 req/min for callers holding a
            // valid ARC-14 token partitioned by their Algorand address so one busy authenticated
            // caller can't starve another. /health is excluded - it's polled every few seconds by
            // k8s probes and the external uptime monitor and would otherwise burn through the
            // anonymous budget by itself.
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                {
                    if (httpContext.Request.Path.StartsWithSegments("/health"))
                    {
                        return RateLimitPartition.GetNoLimiter("health");
                    }

                    // AlgorandAuthenticationHandlerV2 only succeeds (IsAuthenticated == true) for a
                    // validly signed ARC-14 transaction - EmptySuccessOnFailure (which would instead
                    // return a "successful" ticket with an empty identity on a missing/bad token) is
                    // not set in appsettings.json and defaults to false, so this is a reliable signal
                    // here. On success ClaimTypes.Name is the caller's Algorand address (see
                    // AlgorandAuthenticationHandlerV2.VerifyCommon in the AlgorandAuthenticationDotNet
                    // package source).
                    var address = httpContext.User?.Identity?.IsAuthenticated == true
                        ? httpContext.User.Identity.Name
                        : null;
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        return RateLimitPartition.GetFixedWindowLimiter($"auth:{address}", _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 300,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        });
                    }

                    var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"anon:{clientIp}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
                });

                options.OnRejected = async (context, cancellationToken) =>
                {
                    context.HttpContext.Response.Headers.RetryAfter = "60";
                    context.HttpContext.Response.ContentType = "application/json";
                    await context.HttpContext.Response.WriteAsync(
                        "{\"error\":\"Rate limit exceeded. Try again later.\"}", cancellationToken);
                };
            });

            // /health is polled frequently by k8s probes and external monitors, so its checks stay
            // cheap (in-memory cache reads + a single ES/Redis ping) rather than deep data queries.
            builder.Services.AddHealthChecks()
                .AddCheck<HealthChecks.ElasticsearchHealthCheck>("elasticsearch")
                .AddCheck<HealthChecks.AssetCacheHealthCheck>("asset-cache")
                .AddCheck<HealthChecks.PoolCacheHealthCheck>("pool-cache");

            var app = builder.Build();

            // ingress-nginx terminates the client connection and proxies to this pod's ClusterIP,
            // so without this every request's RemoteIpAddress would be the ingress pod's IP -
            // collapsing the per-client rate limiter (and any future per-IP logic) into one shared
            // bucket for all external callers. KnownNetworks/KnownProxies are cleared because the
            // proxy is a dynamic in-cluster pod IP, not a fixed address to allowlist: this pod is
            // only reachable via its ClusterIP Service, so anything that reaches it already went
            // through the ingress.
            var forwardedHeadersOptions = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            };
            forwardedHeadersOptions.KnownIPNetworks.Clear();
            forwardedHeadersOptions.KnownProxies.Clear();
            app.UseForwardedHeaders(forwardedHeadersOptions);

            // Configure the HTTP request pipeline
            // Compression first so that all subsequent response-producing middleware is compressed
            app.UseResponseCompression();

            // CORS must be before authentication/authorization
            app.UseCors();

            // Emit OpenAPI 3.1 (Microsoft.OpenApi 2.x, pulled in by Swashbuckle 10.x, supports 3.1 output).
            app.UseSwagger(o => o.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);
            app.UseSwaggerUI();

            // WebSockets must be before authentication for SignalR
            app.UseWebSockets();

            // Add middleware for SignalR authentication - move access_token from query to header
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/biatecScanHub"))
                {
                    app.Logger.LogDebug($"SignalR request: {context.Request.Method} {context.Request.Path}");
                    app.Logger.LogDebug($"Query: {context.Request.QueryString}");

                    // Check for access_token in query and move to Authorization header
                    if (context.Request.Query.ContainsKey("access_token"))
                    {
                        var accessToken = context.Request.Query["access_token"].ToString();
                        app.Logger.LogDebug($"Access token in query: {accessToken.Substring(0, Math.Min(30, accessToken.Length))}...");

                        // Move to Authorization header for our authentication handler
                        if (!context.Request.Headers.ContainsKey("Authorization"))
                        {
                            // URL decode the token
                            var decodedToken = System.Web.HttpUtility.UrlDecode(accessToken);
                            context.Request.Headers["Authorization"] = decodedToken;
                            app.Logger.LogDebug($"Moved and decoded access_token to Authorization header: {decodedToken.Substring(0, Math.Min(30, decodedToken.Length))}...");
                        }
                    }

                    // Debug: Show all headers
                    app.Logger.LogDebug("Request Headers:");
                    foreach (var header in context.Request.Headers)
                    {
                        app.Logger.LogDebug($"  {header.Key}: {header.Value}");
                    }
                }

                await next();

                if (context.Request.Path.StartsWithSegments("/biatecScanHub"))
                {
                    app.Logger.LogDebug($"Response status: {context.Response.StatusCode}");
                    if (context.User?.Identity != null)
                    {
                        app.Logger.LogDebug($"User authenticated: {context.User.Identity.IsAuthenticated}, Name: '{context.User.Identity.Name}'");
                        if (context.User.Claims != null)
                        {
                            app.Logger.LogDebug("User Claims:");
                            foreach (var claim in context.User.Claims)
                            {
                                app.Logger.LogDebug($"  {claim.Type}: {claim.Value}");
                            }
                        }
                    }
                }
            });

            app.UseAuthentication();
            app.UseAuthorization();

            // Must come after UseAuthentication so the "address" claim is populated for the
            // authenticated-vs-anonymous rate limit tiers configured in AddRateLimiter above.
            app.UseRateLimiter();

            app.MapControllers();
            app.MapHub<BiatecScanHub>("/biatecScanHub");

            // Index page: redirect to the Swagger UI. ExcludeFromDescription keeps this
            // endpoint itself out of the generated OpenAPI document.
            app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

            // Used by k8s startup/readiness/liveness probes and the external uptime monitor.
            // Microsoft.* logging is capped at Warn in nlog.config, so this high-frequency polling
            // never reaches the console/log output. Degraded (e.g. an empty cache right after a
            // cold Redis/ES outage) still returns 200 so the pod stays in rotation; only Unhealthy
            // (a downstream dependency ping actually failing) returns 503.
            app.MapHealthChecks("/health", new HealthCheckOptions
            {
                ResponseWriter = HealthChecks.HealthCheckResponseWriter.WriteResponse,
                AllowCachingResponses = false,
                ResultStatusCodes =
                {
                    [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
                    [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
                    [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
                },
            }).ExcludeFromDescription();

            // initialize all singletons

            using var cancellationTokenSource = new CancellationTokenSource();

            var assetRepo = app.Services.GetService<IAssetRepository>() as AssetRepository ?? throw new Exception("AssetRepository not initialized");
            assetRepo.EnsureInitializedAsync(cancellationTokenSource.Token).Wait();
            _ = app.Services.GetService<AggregatedPoolRepository>() ?? throw new Exception("aggregatedPoolRepository not initialized");
            var poolRepository = app.Services.GetService<IPoolRepository>() as PoolRepository ?? throw new Exception("Pool repository not initialized");
            poolRepository.InitializeAsync(cancellationTokenSource.Token).Wait();

            _ = app.Services.GetService<IDefaultApi>();
            _ = app.Services.GetService<BlockRepository>();
            _ = app.Services.GetService<IndexerRepository>();
            _ = app.Services.GetService<TradeRepository>();
            _ = app.Services.GetService<LiquidityRepository>();
            _ = app.Services.GetService<TransactionProcessor>();
            _ = app.Services.GetService<PactPoolProcessor>();
            _ = app.Services.GetService<TinyPoolProcessor>();
            _ = app.Services.GetService<BiatecPoolProcessor>();
            _ = app.Services.GetService<OHLCRepository>();
            _ = app.Services.GetService<TvlOhlcRepository>();
            _ = app.Services.GetService<ISearchService>();
            _ = app.Services.GetService<ITradeQueryService>();

            var bw = app.Services.GetService<TradeReporterBackgroundService>();
            bw?.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            // GossipBackgroundService is now resolvable via DI (registered as a singleton above so
            // GossipController can query it), so it must NOT also be started manually here - the generic
            // host already starts every registered IHostedService (including it) when app.Run() below
            // begins; starting it a second time here would run ExecuteAsync twice concurrently.

            app.Run();
        }
    }
}
