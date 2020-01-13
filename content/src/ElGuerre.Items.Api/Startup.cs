﻿using ElGuerre.Items.Api.Application.Services;
using ElGuerre.Items.Api.Domain.Interfaces;
using ElGuerre.Items.Api.Filters;
using ElGuerre.Items.Api.Infrastructure;
using ElGuerre.Items.Api.Infrastructure.Filters;
using ElGuerre.Items.Api.Infrastructure.Http;
using ElGuerre.Items.Api.Infrastructure.Repositories;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.AzureAD.UI;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Logging;
using Swashbuckle.AspNetCore.Swagger;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Claims;
using System.Threading.Tasks;

namespace ElGuerre.Items.Api
{
	public class Startup
	{
		public readonly IConfiguration _configuration;
		private readonly IHostingEnvironment _environment;
		private readonly ILoggerFactory _loggerFactory;
		public readonly AppSettings _settings;

		public Startup(IConfiguration configuration, ILoggerFactory loggerFactory, IHostingEnvironment environment)
		{
			_configuration = configuration;
			_loggerFactory = loggerFactory;
			_environment = environment;
			_settings = _configuration.GetSection(Program.AppName).Get<AppSettings>();
		}

		public IConfiguration Configuration { get; }

		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services)
		{
			var isMock = _environment.IsEnvironment("Mock");

			services
				.AddCustomApplicationInsights(_configuration)
				.AddCustomHealthChecks(_configuration)
				.AddCustomDbContext(_configuration, _settings)
				.AddCustomServices(isMock)
				.AddCustomSwagger()
				.AddCustomConfiguration(_configuration)
				.AddCustomAuthentication(_loggerFactory, _environment, _configuration)
				.AddCustomMVC(_environment);
		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IHostingEnvironment env)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}
			else
			{
				// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
				app.UseHsts();
			}

			app.UseHealthChecks("/liveness", new HealthCheckOptions
			{
				Predicate = r => r.Name.Contains("self")
			});

			app.UseHealthChecks("/hc", new HealthCheckOptions()
			{
				Predicate = _ => true,
				ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
			});

			app.UseSwagger()
				.UseSwaggerUI(c =>
				{
					c.SwaggerEndpoint("/swagger/v1/swagger.json", $"{Program.Namespace} v1.0.0");
				});

			app.UseHttpsRedirection();
			app.UseAuthentication();

			app.UseMvc();
		}
	}

	internal static class StartupExtensionMethods
	{		
		public static IServiceCollection AddCustomApplicationInsights(this IServiceCollection services, IConfiguration configuration)
		{
			services.AddApplicationInsightsTelemetry(configuration);

			var orchestratorType = configuration.GetValue<string>("OrchestratorType");
			if (orchestratorType?.ToUpper() == "K8S")
			{
				// Enable K8s telemetry initializer
				services.AddApplicationInsightsKubernetesEnricher();
			}

			return services;
		}

		public static IServiceCollection AddCustomMVC(this IServiceCollection services, IHostingEnvironment env)
		{
			services.AddMvc(options =>
			{
				// ***************************************************************************
				// TODO: Comment to enabled/disabled authorization for all Controller's actions
				// ***************************************************************************
				if (env.IsEnvironment("Mock"))
				{
					var policy = new AuthorizationPolicyBuilder()
					.RequireAuthenticatedUser()
					.Build();
					options.Filters.Add(new AuthorizeFilter(policy));
				}


				// Custom Filter to validate BadRequests for all Controllers.
				options.Filters.Add(typeof(ValidateModelState));
				options.Filters.Add(typeof(HttpGlobalExceptionFilter));
			})
						//.AddJsonOptions(opt =>
						//{
						//    opt.SerializerSettings.ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() };
						//})
						.SetCompatibilityVersion(CompatibilityVersion.Version_2_2)
						.AddControllersAsServices();

			services.AddCors(options =>
			{
				options.AddPolicy("CorsPolicy",
					builder => builder
					.SetIsOriginAllowed((host) => true)
					.WithMethods(
						"GET",
						"POST",
						"PUT",
						"DELETE",
						"OPTIONS")
					.AllowAnyHeader()
					.AllowCredentials());
			});

			return services;
		}

		public static IServiceCollection AddCustomConfiguration(this IServiceCollection services, IConfiguration configuration)
		{
			services.AddOptions();
			services.Configure<AppSettings>(_ => configuration.GetSection(Program.AppName).Get<AppSettings>());
			services.Configure<ApiBehaviorOptions>(options =>
			{
				options.InvalidModelStateResponseFactory = context =>
				{
					var problemDetails = new ValidationProblemDetails(context.ModelState)
					{
						Instance = context.HttpContext.Request.Path,
						Status = StatusCodes.Status400BadRequest,
						Detail = "Please refer to the errors property for additional details."
					};

					return new BadRequestObjectResult(problemDetails)
					{
						ContentTypes = { "application/problem+json", "application/problem+xml" }
					};
				};
			});

			return services;
		}

		public static IServiceCollection AddCustomDbContext(this IServiceCollection services, IConfiguration configuration, AppSettings settings)
		{
			services.AddDbContext<ItemsContext>(options =>
			{
				if (configuration.GetValue<bool>("DBInMemory"))
				{
					options.UseInMemoryDatabase(Program.AppName,
						(ops) =>
						{

						});
				}
				else
				{
					options.UseSqlServer(settings.DBConnectionString,
										 sqlServerOptionsAction: sqlOptions =>
										 {
											 sqlOptions.MigrationsAssembly(typeof(Startup).GetTypeInfo().Assembly.GetName().Name);
											 //Configuring Connection Resiliency: https://docs.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency 
											 sqlOptions.EnableRetryOnFailure(maxRetryCount: 10, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
										 });

					// Changing default behavior when client evaluation occurs to throw. 
					// Default in EF Core would be to log a warning when client evaluation is performed.
					options.ConfigureWarnings(warnings => warnings.Throw(RelationalEventId.QueryClientEvaluationWarning));
					//Check Client vs. Server evaluation: https://docs.microsoft.com/en-us/ef/core/querying/client-eval
				}
			});

			return services;
		}

		public static IServiceCollection AddCustomSwagger(this IServiceCollection services)
		{
			services.AddSwaggerGen(options =>
			{
				options.DescribeAllEnumsAsStrings();
				options.EnableAnnotations();
				options.SwaggerDoc("v1", new Info
				{
					Version = "v1.0.0",
					Title = $"{Program.Namespace}",
					Description = $"API to expose logic for {Program.AppName} service",
					TermsOfService = ""
				});
				var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
				var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
				options.IncludeXmlComments(xmlPath);

				options.AddSecurityDefinition("Bearer",
				 new ApiKeyScheme
				 {
					 In = "header",
					 Description = "Please enter into field the word 'Bearer' following by space and JWT",
					 Name = "Authorization",
					 Type = "apiKey"
				 });

				options.AddSecurityRequirement(new Dictionary<string, IEnumerable<string>> {
					{ "Bearer", Enumerable.Empty<string>() },
				});
			});

			return services;
		}

		public static IServiceCollection AddCustomServices(this IServiceCollection services, bool isMock)
		{			
			services.AddHttpContextAccessor();
			
			// Used to call External API Rests !
			services.AddScoped<IHttpClient, StandardHttpClient>();

			if (isMock)
			{

			}
			else
			{
				services.AddTransient<IItemsRepository, ItemsRepository>();
				services.AddTransient<IItemsService, ItemsService>();
			}

			return services;
		}

		public static IServiceCollection AddCustomHealthChecks(this IServiceCollection services, IConfiguration configuration)
		{
			var hcBuilder = services.AddHealthChecks();

			hcBuilder.AddCheck("self", () => HealthCheckResult.Healthy());

			//hcBuilder
			//	.AddSqlServer(
			//		configuration[$"{Program.AppName}:{nameof(AppSettings.OrdersDBConnectionString)}"],
			//		name: "OrderingDB-check",
			//		tags: new string[] { "orderingdb" });

			//if (configuration.GetValue<bool>("AzureServiceBusEnabled"))
			//{
			//hcBuilder
			//	.AddAzureServiceBusTopic(
			//		configuration[$"{Program.AppName}:{nameof(AppSettings.EventBusConnectionString)}"],
			//		topicName: "Items_event_bus",
			//		name: "Items-servicebus-check",
			//		tags: new string[] { "servicebus" });
			//}

			return services;
		}

		public static IServiceCollection AddCustomAuthentication(
			this IServiceCollection services,
			ILoggerFactory loggerFactory,
			IHostingEnvironment env,
			IConfiguration configuration)
		{
			var configSectionName = "AzureAd";

			var logger = loggerFactory.CreateLogger("Authentication");

			services.AddAuthentication(AzureADDefaults.JwtBearerAuthenticationScheme)
					.AddAzureADBearer(options => configuration.Bind(configSectionName, options));

			// Change the authentication configuration to accommodate the Microsoft identity platform endpoint (v2.0).
			services.Configure<JwtBearerOptions>(AzureADDefaults.JwtBearerAuthenticationScheme, options =>
			{
				// This is an Microsoft identity platform Web API
				options.Authority += "/v2.0";

				options.TokenValidationParameters.ValidateIssuer = false; // accept several tenants (here simplified)

				// The valid audiences are both the Client ID (options.Audience) and api://{ClientID}
				options.TokenValidationParameters.ValidAudiences = new string[]
				{
					options.Audience,
					$"api://{options.Audience}",
					$"https://{configuration[$"{configSectionName}:Domain"]}/{Program.Namespace}"
				};

				if (env.IsDevelopment())
				{
					IdentityModelEventSource.ShowPII = true;
				}

				// Instead of using the default validation (validating against a single tenant, as we do in line of business apps),
				// we inject our own multi-tenant validation logic (which even accepts both v1.0 and v2.0 tokens)
				// options.TokenValidationParameters.IssuerValidator = AadIssuerValidator.GetIssuerValidator(options.Authority).Validate;

				options.Events = new JwtBearerEvents
				{
					OnAuthenticationFailed = context =>
					{
						logger.LogError($"Auth error: {context.Exception.InnerException}");
						throw new AuthenticationException("Invalid or expired token", context.Exception.InnerException);
					},
					OnMessageReceived = context =>
					{
						return Task.CompletedTask;
					},
					OnChallenge = context =>
					{
						return Task.CompletedTask;
					},
					OnTokenValidated = async context =>
					{
						logger.LogInformation("Token validated");

						// Add the access_token as a claim, as we may actually need it
						if (context.SecurityToken is JwtSecurityToken idToken && context.Principal.Identity is ClaimsIdentity identity)
						{
							var email = context.Principal.Claims.First(c => c.Type == ClaimTypes.Email).Value;

							if (string.IsNullOrWhiteSpace(email))
							{
								throw new AuthenticationException("No user available");
							}

							// TODO: Obtener y validar información adicional del usuario (según su email) y popular las Claims adecuadas para usar a lo largo del API.		                            
							// 1) Llmar al servicio de MdA y obtener el IdPersona (MdA)                            
							// 2) Implementar a nivel de Application una clase estática similar a la clase ClaimTypes
							// 2.1) ClaimTypesMdA.IdPersona con el valor: "http://mda.nsi/IdPersona"
							// 3) Crear una nueva clain con el IdPersona y añadirla a la idenditad: identity.AddClaim(new Claim(ClaimTypesMdA.ID_PERSONA, "## IdPersona ##"));
							// En los Controllers y en el resto de la aplicación donde haya que utlizar el IdPersona se hará vía Contex/Identity.
							//
							// TODO: Añadir otras Claims
							// identity.AddClaim(new Claim(ClaimTypes.GivenName, yyyyy));
							// 

							await Task.FromResult(0);
						}
					}
				};
			});

			return services;
		}

	}
}
