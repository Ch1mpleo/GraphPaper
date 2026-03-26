using GraphPaper.Application;
using GraphPaper.Application.Interfaces;
using GraphPaper.Application.Services;
using GraphPaper.Domain;
using GraphPaper.Domain.Entities;
using GraphPaper.Infrastructure;
using GraphPaper.Infrastructure.Commons;
using GraphPaper.Infrastructure.Interfaces;
using GraphPaper.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;

namespace GraphPaper.API.Architecture;

public static class IocContainer
{
    public static IServiceCollection SetupIocContainer(this IServiceCollection services)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", true, true)
            .AddEnvironmentVariables()
            .Build();

        services.SetupDbContext();
        services.SetupSwagger();
        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        services.SetupBusinessServicesLayer();
        services.SetupAiServices(configuration);
        services.SetupJwt();

        return services;
    }

    private static IServiceCollection SetupDbContext(this IServiceCollection services)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", true, true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<GraphPaperDbContext>(options =>
            options.UseNpgsql(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(GraphPaperDbContext).Assembly.FullName);
                sql.UseVector();
            })
        );

        return services;
    }

    public static IServiceCollection SetupBusinessServicesLayer(this IServiceCollection services)
    {
        services.AddScoped<ICurrentTime, CurrentTime>();
        services.AddScoped<IClaimsService, ClaimsService>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
        services.AddScoped<IDocumentReviewService, DocumentReviewService>();
        services.AddScoped<IMindmapService, MindmapService>();
        services.AddScoped<IAuthService, AuthService>();

        services.AddHttpContextAccessor();

        return services;
    }

    private static IServiceCollection SetupSwagger(this IServiceCollection services)
    {
        services.AddSwaggerGen(c =>
        {
            c.UseInlineDefinitionsForEnums();

            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "GraphPaperAPI",
                Version = "v1",
                Description = "API for NotebookLM-like chatbot extract data from PDF into GraphRAG",
            });

            var jwtSecurityScheme = new OpenApiSecurityScheme
            {
                Name = "JWT Authentication",
                Description = "Enter your JWT token in this field",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            };

            c.AddSecurityDefinition("Bearer", jwtSecurityScheme);

            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });

            c.UseAllOfForInheritance();
            c.EnableAnnotations();
        });

        return services;
    }

    private static IServiceCollection SetupJwt(this IServiceCollection services)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", true, true)
            .AddEnvironmentVariables()
            .Build();

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(x =>
            {
                x.SaveToken = true;
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidIssuer = configuration["JWT:Issuer"],
                    ValidAudience = configuration["JWT:Audience"],
                    IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                            configuration["JWT:SecretKey"] ?? throw new InvalidOperationException()))
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("CustomerPolicy", policy =>
                policy.RequireRole(User.RoleCustomer));
        });

        return services;
    }

    private static IServiceCollection SetupAiServices(this IServiceCollection services, IConfiguration configuration)
    {
        var geminiApiKey = configuration["GEMINI_API_KEY"]
                           ?? throw new InvalidOperationException("Gemini API Key is missing.");
        var ollamaBaseUrl = configuration["OLLAMA_BASE_URL"] ?? "http://host.docker.internal:11434";
        var ollamaModel = configuration["OLLAMA_MODEL"] ?? "llama3.1:8b";

        // Bind DocumentProcessingOptions from appsettings.json section
        services.Configure<DocumentProcessingOptions>(
            configuration.GetSection(DocumentProcessingOptions.Section));

        // Named HttpClients managed by IHttpClientFactory (avoids socket exhaustion)
        services.AddHttpClient("Gemini", client =>
        {
            client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("GeminiKnowledge", client =>
        {
            client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            client.Timeout = TimeSpan.FromMinutes(2);
        });

        services.AddSingleton<OpenXmlDocumentParser>();

        services.AddHttpClient("Ollama", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddHttpClient("GroqKnowledge", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddHttpClient("Docling", client =>
        {
            client.BaseAddress = new Uri("http://graphpaper.parser:5001");
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        services.AddScoped<IDoclingClient, DoclingClient>();

        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var options = sp.GetRequiredService<IOptions<DocumentProcessingOptions>>().Value;
            return new GeminiEmbeddingService(factory, geminiApiKey, options);
        });

        services.AddSingleton<IKnowledgeExtractionService>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var options = sp.GetRequiredService<IOptions<DocumentProcessingOptions>>().Value;
            return new OllamaKnowledgeExtractionService(factory, options, ollamaBaseUrl, ollamaModel);
        });

        services.AddScoped<IRelationshipEnrichmentService>(sp =>
        {
            var uow     = sp.GetRequiredService<IUnitOfWork>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var embed   = sp.GetRequiredService<IEmbeddingService>();
            var opts    = sp.GetRequiredService<IOptions<DocumentProcessingOptions>>().Value;
            var logger  = sp.GetRequiredService<ILogger<OllamaRelationshipEnrichmentService>>();
            return new OllamaRelationshipEnrichmentService(uow, factory, embed, opts, logger, ollamaBaseUrl, ollamaModel);
        });

        return services;
    }
}