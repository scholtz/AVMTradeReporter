using Algorand;
using Algorand.Algod;
using AlgorandAuthenticationV2;
using AVMIndexReporter.Repository;
using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Security;
using Elastic.Transport;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;

namespace AVMTradeReporter
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
                {
                    Title = "AVM Trade Reporter API",
                    Version = "v1",
                    Description = File.ReadAllText("doc/description.md"),
                });
                c.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
                {
                    Description = "ARC-0014 Algorand authentication transaction",
                    In = ParameterLocation.Header,
                    Name = "Authorization",
                    Type = SecuritySchemeType.ApiKey,
                });
                c.OperationFilter<Swashbuckle.AspNetCore.Filters.SecurityRequirementsOperationFilter>();
                c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First()); //This line
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
            builder.Services.AddSingleton<IndexerRepository>();
            builder.Services.AddSingleton<IPoolRepository, PoolRepository>();
            builder.Services.AddSingleton<PoolRepository>();
            builder.Services.AddSingleton<AggregatedPoolRepository>();
            builder.Services.AddSingleton<TradeRepository>();
            builder.Services.AddSingleton<LiquidityRepository>();
            builder.Services.AddSingleton<TransactionProcessor>();
            builder.Services.AddSingleton<OHLCRepository>(); // register OHLC repository
            builder.Services.AddSingleton<ISearchService, SearchService>();
            builder.Services.AddSingleton<ITradeQueryService, TradeQueryService>();
            builder.Services.AddSingleton<ILiquidityQueryService, LiquidityQueryService>();
            builder.Services.AddSingleton<IOHLCService, OHLCService>();

            // Add Pool Processors
            builder.Services.AddSingleton<PactPoolProcessor>();
            builder.Services.AddSingleton<TinyPoolProcessor>();
            builder.Services.AddSingleton<BiatecPoolProcessor>();

            // Register the background services
            builder.Services.AddHostedService<TradeReporterBackgroundService>();
            builder.Services.AddHostedService<GossipBackgroundService>();

            // Register Pool Refresh Background Service only if enabled
            if (appConfig?.PoolRefresh?.Enabled == true)
            {
                builder.Services.AddHostedService<PoolRefreshBackgroundService>();
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
                .DefaultMappingFor<Model.Data.Trade>(m => m
                    .IndexName("trades")
                    .IdProperty(t => t.TxId))
                .DefaultMappingFor<Model.Data.Liquidity>(m => m
                    .IndexName("liquidity")
                    .IdProperty(t => t.TxId))
                .DefaultMappingFor<Model.Data.AggregatedPool>(m => m
                    .IndexName("liquidity")
                    .IdProperty(t => t.Id))
                .DefaultMappingFor<Model.Data.Pool>(m => m
                    .IndexName("pools")
                    .IdProperty(t => t.PoolAddress))
                .DefaultMappingFor<Model.Data.Indexer>(m => m
                    .IndexName("indexers")
                    .IdProperty(t => t.Id))
                .DefaultMappingFor<Model.Data.OHLC>(m => m
                    .IndexName("ohlc")
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

            var app = builder.Build();

            // Configure the HTTP request pipeline
            // CORS must be before authentication/authorization
            app.UseCors();

            app.UseSwagger();
            app.UseSwaggerUI();

            // WebSockets must be before authentication for SignalR
            app.UseWebSockets();

            // Add middleware for SignalR authentication - move access_token from query to header
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/biatecScanHub"))
                {
                    Console.WriteLine($"SignalR request: {context.Request.Method} {context.Request.Path}");
                    Console.WriteLine($"Query: {context.Request.QueryString}");

                    // Check for access_token in query and move to Authorization header
                    if (context.Request.Query.ContainsKey("access_token"))
                    {
                        var accessToken = context.Request.Query["access_token"].ToString();
                        Console.WriteLine($"Access token in query: {accessToken.Substring(0, Math.Min(30, accessToken.Length))}...");

                        // Move to Authorization header for our authentication handler
                        if (!context.Request.Headers.ContainsKey("Authorization"))
                        {
                            // URL decode the token
                            var decodedToken = System.Web.HttpUtility.UrlDecode(accessToken);
                            context.Request.Headers["Authorization"] = decodedToken;
                            Console.WriteLine($"Moved and decoded access_token to Authorization header: {decodedToken.Substring(0, Math.Min(30, decodedToken.Length))}...");
                        }
                    }

                    // Debug: Show all headers
                    Console.WriteLine("Request Headers:");
                    foreach (var header in context.Request.Headers)
                    {
                        Console.WriteLine($"  {header.Key}: {header.Value}");
                    }
                }

                await next();

                if (context.Request.Path.StartsWithSegments("/biatecScanHub"))
                {
                    Console.WriteLine($"Response status: {context.Response.StatusCode}");
                    if (context.User?.Identity != null)
                    {
                        Console.WriteLine($"User authenticated: {context.User.Identity.IsAuthenticated}, Name: '{context.User.Identity.Name}'");
                        if (context.User.Claims != null)
                        {
                            Console.WriteLine("User Claims:");
                            foreach (var claim in context.User.Claims)
                            {
                                Console.WriteLine($"  {claim.Type}: {claim.Value}");
                            }
                        }
                    }
                }
            });

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();
            app.MapHub<BiatecScanHub>("/biatecScanHub");

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
            _ = app.Services.GetService<ISearchService>();
            _ = app.Services.GetService<ITradeQueryService>();

            var bw = app.Services.GetService<TradeReporterBackgroundService>();
            bw?.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var bwG = app.Services.GetService<GossipBackgroundService>();
            bwG?.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            app.Run();
        }
    }
}
