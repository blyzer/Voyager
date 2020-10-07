﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Voyager.Api
{
	public class DefaultModelBinder : ModelBinder
	{
		private readonly IOptions<JsonOptions> jsonOptions;
		private readonly TypeBindingRepository typeBindingRepo;

		public DefaultModelBinder(TypeBindingRepository typeBindingRepo, IOptionsSnapshot<JsonOptions> jsonOptions)
		{
			this.typeBindingRepo = typeBindingRepo;
			this.jsonOptions = jsonOptions;
			jsonOptions.Value.JsonSerializerOptions.MaxDepth = 0;
		}

		public async Task<TRequest> Bind<TRequest>(HttpContext context)
		{
			return (TRequest)await BindInternal(context, typeof(TRequest));
		}

		public async Task<TRequest> Bind<TRequest, TResponse>(HttpContext context)
		{
			return (TRequest)await BindInternal(context, typeof(TRequest));
		}

		public Task<object> Bind(HttpContext context, Type returnType)
		{
			return BindInternal(context, returnType);
		}

		private async Task<object> BindInternal(HttpContext context, Type returnType)
		{
			var mediatorRequest = Activator.CreateInstance(returnType);
			var routeProvider = new RouteValueProvider(BindingSource.Path, context.Request.RouteValues);
			var queryProvider = new QueryStringValueProvider(BindingSource.Query, context.Request.Query, CultureInfo.InvariantCulture);
			var compositeValueProvider = new CompositeValueProvider
			{
				routeProvider,
				queryProvider
			};
			IValueProvider formProvider = null;
			var bodyProvider = new JsonBodyValueProvider(await ParseBody(context));
			if (context.Request.HasFormContentType)
			{
				formProvider = new FormValueProvider(BindingSource.Form, context.Request.Form, CultureInfo.CurrentCulture); ;
				compositeValueProvider.Add(formProvider);
			}
			else
			{
				compositeValueProvider.Add(bodyProvider);
			}

			IValueProvider GetProvider(BoundProperty property)
			{
				if (property.BindingSource == BindingSource.Path)
				{
					return routeProvider;
				}
				else if (property.BindingSource == BindingSource.Query)
				{
					return queryProvider;
				}
				else if (property.BindingSource == BindingSource.Form)
				{
					return formProvider;
				}
				else if (property.BindingSource == BindingSource.Body)
				{
					return bodyProvider;
				}
				return compositeValueProvider;
			}

			foreach (var property in typeBindingRepo.GetProperties(returnType))
			{
				var propType = property.Property.PropertyType;
				var valueProvider = GetProvider(property);
				if (valueProvider != null)
				{
					var value = valueProvider.GetValue(property.Name);
					if (value.FirstValue != null)
					{
						if (propType == typeof(string))
						{
							property.Property.SetValue(mediatorRequest, value.FirstValue);
						}
						else if (propType == typeof(bool))
						{
							property.Property.SetValue(mediatorRequest, Convert.ToBoolean(value.FirstValue));
						}
						else if (propType == typeof(char))
						{
							property.Property.SetValue(mediatorRequest, Convert.ToChar(value.FirstValue));
						}
						else if (propType == typeof(DateTime))
						{
							property.Property.SetValue(mediatorRequest, Convert.ToDateTime(value.FirstValue));
						}
						else if (propType == typeof(DateTimeOffset))
						{
							property.Property.SetValue(mediatorRequest, DateTimeOffset.Parse(value.FirstValue));
						}
						else if (propType == typeof(TimeSpan))
						{
							property.Property.SetValue(mediatorRequest, TimeSpan.Parse(value.FirstValue));
						}
						else if (propType == typeof(Uri))
						{
							property.Property.SetValue(mediatorRequest, new Uri(value.FirstValue));
						}
						else if (propType == typeof(Version))
						{
							property.Property.SetValue(mediatorRequest, new Version(value.FirstValue));
						}
						else if (propType == typeof(Guid))
						{
							property.Property.SetValue(mediatorRequest, Guid.Parse(value.FirstValue));
						}
						else if (propType == typeof(decimal) || propType == typeof(double) || propType == typeof(int) || propType == typeof(uint) ||
							propType == typeof(byte) || propType == typeof(sbyte) || propType == typeof(long) || propType == typeof(float) ||
							propType == typeof(short) || propType == typeof(ulong) || propType == typeof(ushort))
						{
							var objValue = JsonSerializer.Deserialize(value.FirstValue, propType, jsonOptions.Value.JsonSerializerOptions);
							property.Property.SetValue(mediatorRequest, objValue);
						}
						else
						{
							var text = bodyProvider.ValueKind == JsonValueKind.String ? $"\"{value.FirstValue}\"" : value.FirstValue;
							var objValue = JsonSerializer.Deserialize(text, propType, jsonOptions.Value.JsonSerializerOptions);
							property.Property.SetValue(mediatorRequest, objValue);
						}
					}
				}
			}
			return mediatorRequest;
		}

		private async Task<Dictionary<string, object>> ParseBody(HttpContext context)
		{
			if (context.Request.HasFormContentType)
			{
				return new Dictionary<string, object>();
			}
			using var reader = new StreamReader(context.Request.Body);
			var body = await reader.ReadToEndAsync();
			if (string.IsNullOrWhiteSpace(body))
			{
				return new Dictionary<string, object>();
			}
			return JsonSerializer.Deserialize<Dictionary<string, object>>(body, jsonOptions.Value.JsonSerializerOptions);
		}
	}
}