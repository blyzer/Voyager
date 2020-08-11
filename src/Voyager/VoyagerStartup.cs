﻿using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Voyager.Api;
using Voyager.Api.Authorization;
using Voyager.Configuration;
using Voyager.Mediatr;
using Voyager.Middleware;
using Voyager.SetProperties;

[assembly: InternalsVisibleTo("Voyager.UnitTests")]

namespace Voyager
{
	internal static class VoyagerStartup
	{
		public static void Configure(VoyagerConfigurationBuilder builder, IServiceCollection services)
		{
			var isRegistered = services.Any(s => s.ImplementationType == typeof(IsRegistered));
			if (!isRegistered)
			{
				services.AddScoped<IsRegistered>();
				services.AddMvcCore().AddApiExplorer();
				services.TryAddEnumerable(ServiceDescriptor.Transient<IApiDescriptionProvider, VoyagerApiDescriptionProvider>());
				services.AddSingleton<ExceptionHandler, DefaultExceptionHandler>();
				var voyagerConfig = new VoyagerConfiguration
				{
					EnvironmentName = Environment.GetEnvironmentVariable("VOYAGER_ENVIRONMENT") ??
						Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "DEVELOPMENT"
				};
				services.AddSingleton(voyagerConfig);
				builder.AddAssemblyWith<VoyagerConfigurationBuilder>();
				services.AddHttpContextAccessor();
				services.TryAddSingleton<ModelBinder, DefaultModelBinder>();
				services.TryAddTransient<PropertySetterFactory, DefaultPropertySetterFactory>();
				services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
				services.AddLogging();
				services.AddSingleton<TypeBindingRepository>();
			}

			AddPropertySetters(services, builder.Assemblies);
			services.AddValidatorsFromAssemblies(builder.Assemblies);
			RegisterMediatorHandlers(services, builder.Assemblies);
			RegisterVoyagerRoutes(services, builder.Assemblies);
			AddCustomAuthorization(services, builder.Assemblies);

			foreach (var assembly in builder.Assemblies)
			{
				services.AddMediatR(assembly);
			}
		}

		internal static IEnumerable<Type> GetAllTypesImplementingType(Type openGenericType, Assembly assembly)
		{
			return from x in assembly.GetTypes()
				   from z in x.GetInterfaces()
				   let y = x.BaseType
				   where
				   y != null && y.IsGenericType &&
				   openGenericType.IsAssignableFrom(y.GetGenericTypeDefinition()) ||
				   z.IsGenericType &&
				   openGenericType.IsAssignableFrom(z.GetGenericTypeDefinition())
				   select x;
		}

		internal static IEnumerable<PolicyDefinition> GetPolicies(IEnumerable<Assembly> assemblies)
		{
			var policies = new Dictionary<string, PolicyDefinition>();
			foreach (var assembly in assemblies)
			{
				foreach (var policyType in assembly.GetTypes().Where(t => !t.IsInterface && typeof(Policy).IsAssignableFrom(t)))
				{
					var name = policyType.FullName;
					var overrideType = policyType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(OverridePolicy<>));
					if (overrideType != null)
					{
						name = overrideType.GetGenericArguments()[0].FullName;
					}
					if (policies.ContainsKey(name) && overrideType == null)
					{
						continue;
					}
					policies[name] = new PolicyDefinition
					{
						Policy = (Policy)Activator.CreateInstance(policyType),
						Name = name
					};
				}
			}
			return policies.Values;
		}

		internal static void RegisterMediatorHandlers(IServiceCollection services, IEnumerable<Assembly> assemblies)
		{
			foreach (var assembly in assemblies)
			{
				foreach (var type in GetAllTypesImplementingType(typeof(IRequestHandler<,>), assembly))
				{
					var interfaceType = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>));
					if (interfaceType != null && !type.IsAbstract)
					{
						if (type.IsGenericType)
						{
							services.AddScoped(type.GetGenericTypeDefinition());
							services.TryAddScoped(interfaceType.GetGenericTypeDefinition(), type.GetGenericTypeDefinition());
						}
						else
						{
							services.AddScoped(type);
							services.TryAddScoped(interfaceType, type);
						}
					}
				}
			}
		}

		internal static void RegisterVoyagerRoutes(IServiceCollection services, IEnumerable<Assembly> assemblies)
		{
			foreach (var assembly in assemblies)
			{
				foreach (var type in assembly.GetTypes().Where(t => typeof(IBaseRequest).IsAssignableFrom(t)))
				{
					if (!type.IsInterface && !type.IsAbstract)
					{
						var routeAttributes = type.GetCustomAttributes<RouteAttribute>();
						foreach (var routeAttribute in routeAttributes)
						{
							services.AddTransient((serviceProvider) => routeAttribute.ToEndpointRoute(type));
						}
					}
				}
			}
		}

		private static void AddCustomAuthorization(IServiceCollection services, IEnumerable<Assembly> assemblies)
		{
			var policyDefinitions = GetPolicies(assemblies);

			services.AddAuthorizationCore(options =>
			{
				foreach (var definition in policyDefinitions)
				{
					options.AddPolicy(definition.Name, policyBuilder =>
					{
						var requirements = definition.Policy.GetRequirements();
						if (requirements is null || requirements.Count == 0)
						{
							policyBuilder.RequireAssertion(c => { return true; });
						}
						else
						{
							policyBuilder.Requirements = requirements;
						}
					});
				}
			});
			foreach (var assembly in assemblies)
			{
				foreach (var type in assembly.GetTypes().Where(t => typeof(IAuthorizationHandler).IsAssignableFrom(t)))
				{
					services.AddScoped(typeof(IAuthorizationHandler), type);
				}
			}
		}

		private static void AddPropertySetters(IServiceCollection services, IEnumerable<Assembly> assemblies)
		{
			foreach (var assembly in assemblies)
			{
				foreach (var type in assembly.GetTypes().Where(t => !t.IsInterface && !t.IsAbstract && typeof(SetPropertyValue).IsAssignableFrom(t)))
				{
					if (type.IsGenericType)
					{
						var serviceType = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType);
						if (serviceType != null)
						{
							services.TryAddSingleton(serviceType.GetGenericTypeDefinition(), type);
						}
					}
					else
					{
						var serviceType = type.GetInterfaces().FirstOrDefault(i => typeof(SetPropertyValue<>) == i.GetGenericTypeDefinition());
						if (serviceType != null)
						{
							services.TryAddSingleton(serviceType, type);
						}
					}
				}
			}
		}

		internal class PolicyDefinition
		{
			public string Name { get; set; }
			public Policy Policy { get; set; }
		}

		// Used to keep track if voyager registration has run at least once. It can be run multiple times with different assemblies.
		private class IsRegistered { }
	}
}