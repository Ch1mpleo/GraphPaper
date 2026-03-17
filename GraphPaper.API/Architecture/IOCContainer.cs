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

        //Add Logger
        //services.AddScoped<ILoggerService, LoggerService>();

        //Add Project Services
        services.SetupDbContext();
        services.SetupSwagger();

        //Add generic repositories
        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));

        //Add business services
        services.SetupBusinessServicesLayer();

        //Add Service
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

        // Lấy connection string từ "DefaultConnection"
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        // Đăng ký DbContext với Npgsql - Postgres
        services.AddDbContext<GraphPaperDbContext>(options =>
            options.UseNpgsql(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(GraphPaperDbContext).Assembly.FullName);
                sql.UseVector();    // Enable vector extension for pgvector
            }
            )
        );

        return services;
    }

    public static IServiceCollection SetupBusinessServicesLayer(this IServiceCollection services)
    {
        // Inject service vào DI container
        services.AddScoped<ICurrentTime, CurrentTime>();
        services.AddScoped<IClaimsService, ClaimsService>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
        services.AddScoped<IDocumentReviewService, DocumentReviewService>();
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

            var securityRequirement = new OpenApiSecurityRequirement
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
                    new string[] { }
                }
            };

            c.AddSecurityRequirement(securityRequirement);

            // Cấu hình Swagger để sử dụng Newtonsoft.Json
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
                    ValidateIssuer = true,   // Bật kiểm tra Issuer
                    ValidateAudience = true, // Bật kiểm tra Audience
                    ValidateLifetime = true,
                    ValidIssuer = configuration["JWT:Issuer"],
                    ValidAudience = configuration["JWT:Audience"],
                    IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["JWT:SecretKey"] ??
                                                                        throw new InvalidOperationException()))
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
        var groqApiKey = configuration["GROQ_API_KEY"]
                         ?? throw new InvalidOperationException("Groq API Key is missing.");

        // Named HttpClients managed by IHttpClientFactory (avoids socket exhaustion)
        services.AddHttpClient("Gemini", client =>
        {
            client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
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
            return new GeminiEmbeddingService(factory, geminiApiKey);
        });

        services.AddSingleton<IKnowledgeExtractionService>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new GroqKnowledgeExtractionService(factory, groqApiKey);
        });

        return services;
    }
}