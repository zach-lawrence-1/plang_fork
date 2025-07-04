﻿using LightInject;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PLang.Building.Model;
using PLang.Building.Parsers;
using PLang.Container;
using PLang.Errors;
using PLang.Errors.Handlers;
using PLang.Errors.Interfaces;
using PLang.Errors.Runtime;
using PLang.Events;
using PLang.Exceptions;
using PLang.Interfaces;
using PLang.Models;
using PLang.Runtime;
using PLang.Services.OutputStream;
using PLang.Services.SigningService;
using PLang.Utils;
using System.ComponentModel;
using System.Net;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using static PLang.Utils.StepHelper;

namespace PLang.Modules.WebserverModule
{
	[Description("Start webserver, write to Body, Header, Cookie, send file to client")]
	public class Program : BaseProgram, IDisposable
	{
		private readonly ILogger logger;
		private readonly IEventRuntime eventRuntime;
		private readonly IPLangFileSystem fileSystem;
		private readonly ISettings settings;
		private readonly IOutputStreamFactory outputStreamFactory;
		private readonly PrParser prParser;
		private readonly IPseudoRuntime pseudoRuntime;
		private readonly IEngine engine;
		private readonly ProgramFactory programFactory;
		private readonly static List<WebserverInfo> listeners = new();
		private bool disposed;

		public Program(ILogger logger, IEventRuntime eventRuntime, IPLangFileSystem fileSystem
			, ISettings settings, IOutputStreamFactory outputStreamFactory
			, PrParser prParser,
			IPseudoRuntime pseudoRuntime, IEngine engine, Modules.ProgramFactory programFactory) : base()
		{
			this.logger = logger;
			this.eventRuntime = eventRuntime;
			this.fileSystem = fileSystem;
			this.settings = settings;
			this.outputStreamFactory = outputStreamFactory;
			this.prParser = prParser;
			this.pseudoRuntime = pseudoRuntime;
			this.engine = engine;
			this.programFactory = programFactory;
		}

		public async Task<WebserverInfo?> ShutdownWebserver(string webserverName)
		{
			var webserverInfo = listeners.FirstOrDefault(p => p.WebserverName == webserverName);
			if (webserverInfo == null)
			{
				await outputStreamFactory.CreateHandler().Write($"Webserver named '{webserverName}' does not exist");
				return null;
			}

			try
			{
				webserverInfo.Listener.Close();
			}
			catch
			{
				webserverInfo.Listener.Abort();
			}
			listeners.Remove(webserverInfo);
			return webserverInfo;
		}

		public async Task<bool> RestartWebserver(string webserverName = "default")
		{
			var webserverInfo = await ShutdownWebserver(webserverName);
			if (webserverInfo == null) return false;

			await StartWebserver(webserverInfo.WebserverName, webserverInfo.Scheme, webserverInfo.Host, webserverInfo.Port,
				webserverInfo.MaxContentLengthInBytes, webserverInfo.DefaultResponseContentEncoding, webserverInfo.SignedRequestRequired, webserverInfo.Routings);

			return true;
		}

		public virtual void Dispose()
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
		}

		protected virtual void ThrowIfDisposed()
		{
			if (this.disposed)
			{
				throw new ObjectDisposedException(this.GetType().FullName);
			}
		}

		public record WebserverInfo(HttpListener Listener, string WebserverName, string Scheme, string Host, int Port,
			long MaxContentLengthInBytes, string DefaultResponseContentEncoding, bool SignedRequestRequired, List<Routing>? Routings)
		{
			public List<Routing>? Routings { get; set; } = Routings;
		}


		public record Routing(string Path, GoalToCallInfo? GoalToCall = null, string[]? Method = null, string ContentType = "text/html",
									Dictionary<string, object?>? Parameters = null, long? MaxContentLength = null, string? DefaultResponseContentEncoding = null);

		[Description("When path is /api, overwite the default ContentType value to application/json unless defined by user")]
		public async Task<IError?> AddRoute(string path, GoalToCallInfo? goalToCall = null, string[]? method = null, string ContentType = "text/html",
									Dictionary<string, object?>? Parameters = null, long? MaxContentLength = null, string? DefaultResponseContentEncoding = null,
									string? webserverName = "default")
		{
			WebserverInfo? webserverInfo = null;
			if (webserverName != null)
			{
				webserverInfo = (listeners.Count == 1) ? listeners[0] : listeners.FirstOrDefault(p => p.WebserverName == webserverName);
				if (webserverInfo == null)
				{
					return new ProgramError($"Could not find {webserverName} webserver. Are you defining the correct name?", goalStep, function);
				}
			}
			else if (listeners.Count > 1)
			{
				return new ProgramError($"There are {listeners.Count} servers, please define which webserver you want to assign this routing to.", goalStep, function,
						FixSuggestion: $"rewrite the step to include the server name e.g. `- {goalStep.Text}, on {listeners[0].WebserverName} webserver");
			}
			else if (listeners.Count == 0)
			{
				return new ProgramError($"There are 0 servers, please define a webserver.", goalStep, function,
						FixSuggestion: $"create a step before adding a route e.g. `- start webserver");
			}

			if (webserverInfo == null) webserverInfo = listeners[0];

			if (method == null) method = ["GET"];

			if (webserverInfo.Routings == null) webserverInfo.Routings = new();
			webserverInfo.Routings.Add(new Routing(path, goalToCall, method, ContentType, Parameters, MaxContentLength, DefaultResponseContentEncoding));

			return null;
		}

		public async Task<WebserverInfo> StartWebserver(string webserverName = "default", string scheme = "http", string host = "localhost",
			int port = 8080, long maxContentLengthInBytes = 4096 * 1024,
			string defaultResponseContentEncoding = "utf-8",
			bool signedRequestRequired = false, List<Routing>? routings = null)
		{
			if (listeners.FirstOrDefault(p => p.WebserverName == webserverName) != null)
			{
				throw new RuntimeException($"Webserver '{webserverName}' already exists. Give it a different name");
			}

			if (listeners.FirstOrDefault(p => p.Port == port) != null)
			{
				throw new RuntimeException($"Port {port} is already in use. Select different port to run on, e.g.\n-Start webserver, port 4687");
			}

			var listener = new HttpListener();
			listener.Prefixes.Add(scheme + "://" + host + ":" + port + "/");
			listener.Start();

			var assembly = Assembly.GetAssembly(this.GetType());
			string version = assembly!.GetName().Version!.ToString();

			var webserverInfo = new WebserverInfo(listener, webserverName, scheme, host, port, maxContentLengthInBytes, defaultResponseContentEncoding, signedRequestRequired, routings);
			listeners.Add(webserverInfo);

			logger.LogDebug($"Listening on {host}:{port}...");
			Console.WriteLine($" - Listening on {host}:{port}...");

			await eventRuntime.RunStartEndEvents(EventType.After, EventScope.StartOfApp);
			KeepAlive(listener, "Webserver");

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
			Task.Run(async () =>
			{
				try
				{
					while (true)
					{

						var httpContext = listener.GetContext();
						var routings = webserverInfo.Routings;

						var request = httpContext.Request;
						var resp = httpContext.Response;

						httpContext.Response.Headers.Add("Server", "plang v" + version);

						if (signedRequestRequired && string.IsNullOrEmpty(request.Headers.Get("X-Signature")))
						{
							await WriteError(httpContext.Response, $"You must sign your request to user this web service. Using plang, you simply say. '- GET http://... sign request");
							continue;
						}



						Goal? goal = null;
						string? goalPath = null;
						string? requestedFile = null;
						var container = new ServiceContainer();
						string contentType = "application/json";
						try
						{

							requestedFile = httpContext.Request.Url?.LocalPath;
							(goalPath, var routing) = GetGoalPath(routings, httpContext.Request);

							if (string.IsNullOrEmpty(goalPath))
							{
								ProcessGeneralRequest(httpContext);
								continue;
							}

							goal = prParser.GetGoal(Path.Join(goalPath, ISettings.GoalFileName));
							if (goal == null)
							{
								await WriteNotfound(resp, $"Goal could not be loaded");
								continue;
							}
							if (httpContext.Request.QueryString.GetValues("__signature__") != null)
							{
								httpContext.Response.AddHeader("X-Goal-Hash", goal.Hash);
								httpContext.Response.AddHeader("X-Goal-Signature", goal.Signature);
								httpContext.Response.StatusCode = 200;
								httpContext.Response.Close();
								continue;
							}

							long maxContentLength = (goal.GoalInfo?.GoalApiInfo != null && goal.GoalInfo?.GoalApiInfo?.MaxContentLengthInBytes != 0) ? goal.GoalInfo?.GoalApiInfo.MaxContentLengthInBytes ?? maxContentLengthInBytes : maxContentLengthInBytes;
							if (httpContext.Request.ContentLength64 > maxContentLength)
							{
								httpContext.Response.StatusCode = 413;
								using (var writer = new StreamWriter(resp.OutputStream, resp.ContentEncoding ?? Encoding.UTF8))
								{
									await writer.WriteAsync($"Content sent to server is to big. Max {maxContentLength} bytes");
									await writer.FlushAsync();
								}
								httpContext.Response.Close();
								continue;
							}

							if (httpContext.Request.IsWebSocketRequest)
							{
								ProcessWebsocketRequest(httpContext);
								continue;
							}

							if (!IsValidMethod(routing, request, goal))
							{
								await WriteError(resp, $"Http method '{request.HttpMethod}' is not allowed on goal {goal.GoalName}");
								continue;
							}

							string encodyName = Encoding.GetEncoding(defaultResponseContentEncoding).BodyName;

							httpContext.Response.ContentEncoding = Encoding.GetEncoding(defaultResponseContentEncoding);
							httpContext.Response.ContentType = $"{routing.ContentType}; charset={encodyName}";
							httpContext.Response.Headers["Content-Type"] = $"{routing.ContentType}; charset={encodyName}"; 

							httpContext.Response.SendChunked = true;
							httpContext.Response.AddHeader("X-Goal-Hash", goal.Hash);
							httpContext.Response.AddHeader("X-Goal-Signature", goal.Signature);
							if (goal.GoalInfo != null && goal.GoalInfo.GoalApiInfo != null)
							{
								if (goal.GoalInfo.GoalApiInfo.ContentEncoding != null)
								{
									var encoding = Encoding.GetEncoding(goal.GoalInfo.GoalApiInfo.ContentEncoding);
									httpContext.Response.ContentEncoding = encoding;
									encodyName = encoding.BodyName;
								}
								if (goal.GoalInfo.GoalApiInfo.ContentType != null)
								{
									httpContext.Response.ContentType = $"{goal.GoalInfo.GoalApiInfo.ContentType}; charset={encodyName}";
									httpContext.Response.Headers["Content-Type"] = $"{goal.GoalInfo.GoalApiInfo.ContentType}; charset={encodyName}";
								}

								if (goal.GoalInfo.GoalApiInfo.NoCacheOrNoStore != null)
								{
									httpContext.Response.Headers["Cache-Control"] = goal.GoalInfo.GoalApiInfo.NoCacheOrNoStore;
								}
								else if (goal.GoalInfo.GoalApiInfo.CacheControlPrivateOrPublic != null || goal.GoalInfo.GoalApiInfo.CacheControlMaxAge != null)
								{
									string? publicOrPrivate = goal.GoalInfo.GoalApiInfo.CacheControlPrivateOrPublic;
									if (publicOrPrivate == null) { publicOrPrivate = "public"; }


									httpContext.Response.Headers["Cache-Control"] = $"{publicOrPrivate}, {goal.GoalInfo.GoalApiInfo.CacheControlMaxAge}";
								}
							}

							logger.LogDebug($"Register container for webserver - AbsoluteAppStartupFolderPath:{goal.AbsoluteAppStartupFolderPath}");

							container.RegisterForPLangWebserver(goal.AbsoluteAppStartupFolderPath, Path.DirectorySeparatorChar.ToString(), httpContext, httpContext.Response.ContentType);
							var context = container.GetInstance<PLangAppContext>();
							context.AddOrReplace(ReservedKeywords.IsHttpRequest, true);

							var engine = container.GetInstance<IEngine>();
							engine.Init(container);
							engine.HttpContext = httpContext;

							engine.AddContext(ReservedKeywords.StartingEngine, this.engine);
							var requestMemoryStack = engine.GetMemoryStack();
							var identityService = container.GetInstance<IPLangIdentityService>();
							var error = await ParseRequest(httpContext, identityService, request.HttpMethod, requestMemoryStack);
							 
							List<CallbackInfo>? callbackInfos = null;
							if (request.Headers["!callback"] != null)
							{
								var identity = programFactory.GetProgram<Modules.IdentityModule.Program>(goalStep);
								var callbackResult = await CallbackHelper.GetCallbackInfos(identity, request.Headers["!callback"]);
								if (callbackResult.Error != null)
								{
									await ShowError(container, callbackResult.Error);
									continue;
								}
								callbackInfos = callbackResult.CallbackInfos;

								var keys = request.Headers.AllKeys.Where(p => p.StartsWith("!"));
								foreach (var key in keys)
								{
									if (key != null && !context.ContainsKey(key))
									{
										context.AddOrReplace(key, request.Headers[key]);
									}
								}
							}

							if (error != null)
							{
								await ShowError(container, error);

								continue;
							}

							(var vars, error) = await engine.RunGoal(goal, 0, callbackInfos);
							if (error != null && error is not IErrorHandled)
							{
								await ShowError(container, error);
								continue;
							}
							else
							{

								var streamFactory = container.GetInstance<IOutputStreamFactory>();
								var stream = streamFactory.CreateHandler().Stream;
								if (resp.OutputStream.CanWrite)
								{
									//stream.Seek(0, SeekOrigin.Begin);
									//stream.CopyTo(resp.OutputStream);
								}
								await resp.OutputStream.FlushAsync();

							}

						}
						catch (Exception ex)
						{
							logger.LogError(ex, @"WebServerError - requestedFile:{0} - goalPath:{1} - goal:{2} - 

Error:
{3}", requestedFile, goalPath, goal, ex.ToString());
							var error = new Error(ex.Message, Key: "WebserverCore", 500, ex);
							try
							{
								var errorHandlerFactory = container.GetInstance<IErrorHandlerFactory>();
								var handler = errorHandlerFactory.CreateHandler();
								await handler.ShowError(error);
								continue;
							}
							catch (Exception ex2)
							{
								Console.WriteLine("Original exception:" + JsonConvert.SerializeObject(ex));
								Console.WriteLine("Exception while handling original exception:" + JsonConvert.SerializeObject(ex2));
							}

						}
						finally
						{
							context.Remove("IsHttpRequest");
							resp.Close();
							container.Dispose();
						}
					}
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Webserver crashed");
				}
			});
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

			return webserverInfo;
		}

		private bool IsValidMethod(Routing routing, HttpListenerRequest request, Goal goal)
		{
			if (routing.Method == null) return false;

			foreach (var method in routing.Method)
			{

				if (routing.Method != null && method.Equals(request.HttpMethod, StringComparison.OrdinalIgnoreCase)) return true;

				if (goal.GoalInfo?.GoalApiInfo != null && request.HttpMethod.Equals(goal.GoalInfo.GoalApiInfo?.Method, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;

		}

		private async Task ShowError(ServiceContainer container, IError error)
		{
			if (error is IUserDefinedError)
			{
				var errorHandlerFactory = container.GetInstance<IErrorHandlerFactory>();
				var handler = errorHandlerFactory.CreateHandler();
				await handler.ShowError(error);
			}
			else
			{
				var errorHandlerFactory = container.GetInstance<IErrorSystemHandlerFactory>();
				var handler = errorHandlerFactory.CreateHandler();
				await handler.ShowError(error);
			}
		}

		private void ProcessGeneralRequest(HttpListenerContext httpContext)
		{
			var requestedFile = httpContext.Request.Url?.LocalPath;
			if (requestedFile == null) return;

			using (var container = new ServiceContainer())
			{
				container.RegisterForPLangWebserver(goal.AbsoluteAppStartupFolderPath, goal.RelativeGoalFolderPath, httpContext, "text/html");

				requestedFile = requestedFile.Replace("/", Path.DirectorySeparatorChar.ToString()).Replace(@"\", Path.DirectorySeparatorChar.ToString());

				var fileSystem = container.GetInstance<IPLangFileSystem>();
				var filePath = Path.Join(fileSystem.GoalsPath!, requestedFile);
				var fileExtension = Path.GetExtension(filePath);
				var mimeType = GetMimeType(fileExtension);

				if (mimeType != null && fileSystem.File.Exists(filePath))
				{
					var buffer = fileSystem.File.ReadAllBytes(filePath);
					httpContext.Response.ContentLength64 = buffer.Length;

					httpContext.Response.ContentType = mimeType;
					httpContext.Response.OutputStream.Write(buffer, 0, buffer.Length);
				}
				else
				{
					httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
				}
				httpContext.Response.OutputStream.Close();
			}
		}
		private string GetMimeType(string extension)
		{
			switch (extension)
			{
				case ".txt": return "text/plain";
				case ".jpg": case ".jpeg": return "image/jpeg";
				case ".png": return "image/png";
				case ".gif": return "image/gif";
				case ".html": return "text/html";
				case ".css": return "text/css";
				case ".js": return "application/javascript";
				case ".mp4": return "video/mp4";
				// add more MIME types here as required
				default: return null;
			}
		}
		private async Task WriteNotfound(HttpListenerResponse resp, string error)
		{
			resp.StatusCode = (int)HttpStatusCode.NotFound;

			await outputStreamFactory.CreateHandler().Write(JsonConvert.SerializeObject(error), "text");

		}
		private async Task WriteError(HttpListenerResponse resp, string error)
		{
			resp.StatusCode = (int)HttpStatusCode.InternalServerError;
			resp.StatusDescription = "Error";
			using (var writer = new StreamWriter(resp.OutputStream, resp.ContentEncoding ?? Encoding.UTF8))
			{
				await writer.WriteAsync(JsonConvert.SerializeObject(error));
				await writer.FlushAsync();
			}
			await outputStreamFactory.CreateHandler().Write(JsonConvert.SerializeObject(error), "text");

		}

		public async Task Redirect(string url)
		{
			if (HttpListenerContext == null)
			{
				throw new HttpListenerException(500, "Context is null. Start a webserver before calling me.");
			}

			HttpListenerContext.Response.Redirect(url);
		}

		public async Task WriteToResponseHeader(Dictionary<string, object> headers)
		{
			if (HttpListenerContext == null)
			{
				throw new HttpListenerException(500, "Context is null. Start a webserver before calling me.");
			}
			foreach (var header in headers)
			{
				if (header.Value == null) continue;
				HttpListenerContext.Response.AddHeader(header.Key, header.Value.ToString());
			}
		}

		[Description("headerKey should be null unless specified by user")]
		public async Task<string?> GetUserIp(string? headerKey = null)
		{
			if (headerKey != null)
			{
				if (HttpListenerContext.Request.Headers != null && HttpListenerContext.Request.Headers.AllKeys.Contains(headerKey))
				{
					return HttpListenerContext.Request.Headers[headerKey];
				}
			}
			return HttpListenerContext.Request.UserHostAddress;
		}

		public async Task<string> GetRequestHeader(string key)
		{
			if (HttpListenerContext == null)
			{
				throw new HttpListenerException(500, "Context is null. Start a webserver before calling me.");
			}

			string? headerValue = HttpListenerContext.Request.Headers[key];
			if (headerValue != null) return headerValue;

			headerValue = HttpListenerContext.Request.Headers[key.ToUpper()];
			if (headerValue != null) return headerValue;

			headerValue = HttpListenerContext.Request.Headers[key.ToLower()];
			if (headerValue != null) return headerValue;

			return "";
		}

		public async Task<string> GetCookie(string name)
		{
			if (HttpListenerContext.Request.Cookies.Count == 0) return "";

			var cookie = HttpListenerContext.Request.Cookies.FirstOrDefault(x => x.Name == name);
			if (cookie == null) return "";
			return cookie.Value;
		}
		public async Task WriteCookie(string name, string value, int expiresInSeconds = 60 * 60 * 24 * 7)
		{
			if (HttpListenerContext == null) return;

			var cookie = new System.Net.Cookie(name, value);
			cookie.Expires = DateTime.Now.AddSeconds(expiresInSeconds);

			HttpListenerContext.Response.Cookies.Add(cookie);
		}


		public async Task DeleteCookie(string name)
		{
			if (HttpListenerContext == null) return;

			var cookie = new Cookie(name, null);
			cookie.Expires = DateTime.Now.AddSeconds(-1);

			HttpListenerContext.Response.Cookies.Add(cookie);
		}

		public async Task SendFileToClient(string path, string? fileName = null)
		{
			var response = HttpListenerContext.Response;
			if (!fileSystem.File.Exists(path))
			{
				response.StatusCode = (int)HttpStatusCode.NotFound;
				using (StreamWriter writer = new StreamWriter(response.OutputStream))
				{
					writer.Write("File not found.");
				}
				response.Close();
				return;
			}

			response.ContentType = GetMimeType(path);

			var fileInfo = fileSystem.FileInfo.New(path);
			response.ContentLength64 = fileInfo.Length;
			if (string.IsNullOrEmpty(fileName)) fileName = fileInfo.Name;

			response.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");

			using (var fs = fileSystem.File.OpenRead(path))
			{
				fs.CopyTo(response.OutputStream);
			}

			response.StatusCode = (int)HttpStatusCode.OK;
			response.Close();
		}


		private (string?, Routing?) GetGoalPath(List<Routing>? routings, HttpListenerRequest request)
		{
			if (request == null || request.Url == null || routings == null) return (null, null);
			foreach (var route in routings)
			{
				if (Regex.IsMatch(request.Url.LocalPath, "^" + route.Path + "$", RegexOptions.IgnoreCase))
				{
					return (GetGoalBuildDirPath(route, request), route);
				}

			}

			return ("", null);
		}

		private string GetGoalBuildDirPath(Routing routing, HttpListenerRequest request)
		{
			if (!string.IsNullOrEmpty(routing.GoalToCall))
			{
				var goal = prParser.GetGoalByAppAndGoalName(fileSystem.RelativeAppPath, routing.GoalToCall);
				if (goal != null)
				{
					return goal.AbsolutePrFolderPath;
				}
			}
			if (request == null || request.Url == null) return "";

			var goalName = request.Url.LocalPath.AdjustPathToOs();

			if (goalName.StartsWith(Path.DirectorySeparatorChar))
			{
				goalName = goalName.Substring(1);
			}
			goalName = goalName.RemoveExtension();
			string goalBuildDirPath = Path.Join(fileSystem.BuildPath, goalName).AdjustPathToOs();
			if (fileSystem.Directory.Exists(goalBuildDirPath)) return goalBuildDirPath;

			logger.LogDebug($"Path doesnt exists - goalBuildDirPath:{goalBuildDirPath}");
			return "";

		}

		private async Task<IError?> ParseRequest(HttpListenerContext? context, IPLangIdentityService identityService, string? method, MemoryStack memoryStack)
		{
			if (context == null) return new Error("context is empty");

			var request = context.Request;
			string contentType = request.ContentType ?? "application/json";
			if (string.IsNullOrWhiteSpace(contentType))
			{
				throw new HttpRequestException("ContentType is missing");
			}
			if (method == null) return new Error("Could not map request to api");

			if (request.HttpMethod != method)
			{
				return new Error($"Only {method} is supported. You sent {request.HttpMethod}");
			}
			string body = "";
			using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
			{
				body = await reader.ReadToEndAsync();
			}
			var error = await VerifySignature(request, body, memoryStack);
			if (error != null) return error;

			var nvc = request.QueryString;
			if (contentType.StartsWith("application/json") && !string.IsNullOrEmpty(body))
			{
				var obj = JsonConvert.DeserializeObject(body) as JObject;

				if (nvc.AllKeys.Length > 0)
				{
					if (obj == null) obj = new JObject();
					foreach (var key in nvc.AllKeys)
					{
						if (key == null) continue;
						obj.Add(key, nvc[key]);
					}
				}

				memoryStack.Put("request", obj, goalStep: goalStep);


				return null;
			}

			if (contentType.StartsWith("application/x-www-form-urlencoded") && !string.IsNullOrEmpty(body))
			{
				var formData = HttpUtility.ParseQueryString(body);
				if (nvc.AllKeys.Length > 0)
				{
					if (formData == null)
					{
						formData = nvc;
					}
					else
					{
						foreach (var key in nvc.AllKeys)
						{
							if (key == null) continue;
							formData.Add(key, nvc[key]);
						}
					}
				}
				memoryStack.Put("request", formData, goalStep: goalStep);
				return null;
			}


			memoryStack.Put("request", nvc, goalStep: goalStep);
			return null;

			/*
			 * @ingig - Not really sure what is happening here, so decide to remove it for now. 
			if (request.HttpMethod == method && contentType.StartsWith("multipart/form-data"))
			{
				var boundary = GetBoundary(MediaTypeHeaderValue.Parse(request.ContentType), 70);
				var multipart = new MultipartReader(boundary, request.InputStream);

				while (true)
				{
					var section = await multipart.ReadNextSectionAsync();
					if (section == null) break;

					var formData = section.AsFormDataSection();
					memoryStack.Put(formData.Name, await formData.GetValueAsync());
				}
			}
			*/

		}

		public async Task<IError?> VerifySignature(HttpListenerRequest request, string body, MemoryStack memoryStack)
		{

			string? signatureAsBase64 = request.Headers.Get("X-Signature");
			if (string.IsNullOrWhiteSpace(signatureAsBase64))
			{
				return null;
			}

			string? signatureAsJson = Encoding.UTF8.GetString(Convert.FromBase64String(signatureAsBase64));

			var headers = new Dictionary<string, object?>();

			headers.Add("url", request.Url?.PathAndQuery);
			headers.Add("method", request.HttpMethod);

			var result = await programFactory.GetProgram<Modules.IdentityModule.Program>(goalStep).VerifySignature(signatureAsJson, headers, body, null);
			if (result.Error != null) return result.Error;

			var signature = result.Signature;
			if (signature != null)
			{
				context.AddOrReplace(ReservedKeywords.Signature, signature);
				memoryStack.Put(ReservedKeywords.Identity, signature.Identity, goalStep: goalStep);
			}
			return null;
		}

		private async Task ProcessWebsocketRequest(HttpListenerContext httpContext)
		{
			/*
			 * Not tested, so remove for now.
			 * 
			HttpListenerWebSocketContext webSocketContext = await httpContext.AcceptWebSocketAsync(subProtocol: null);
			WebSocket webSocket = webSocketContext.WebSocket;

			try
			{

				var outputStream = new WebsocketOutputStream(webSocket, signingService);
				var container = new ServiceContainer();
				container.RegisterForPLangWebserver(goal.AbsoluteAppStartupFolderPath, goal.RelativeGoalFolderPath, httpContext);

				var context = container.GetInstance<PLangAppContext>();
				context.Add(ReservedKeywords.IsHttpRequest, true);

				var engine = container.GetInstance<IEngine>();
				engine.Init(container);
				engine.HttpContext = httpContext;

				byte[] buffer = new byte[1024];

				while (webSocket.State == WebSocketState.Open)
				{
					var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

					if (result.MessageType == WebSocketMessageType.Close)
					{
						await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
					}
					else if (result.MessageType == WebSocketMessageType.Text)
					{
						string receivedMessage = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
					}
				}
				await engine.RunGoal(goal);

				await webSocket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), CancellationToken.None);
			}
			catch (Exception e)
			{
				logger.LogError(e.Message, e);
				Console.WriteLine("Exception: " + e.Message);
			}
			finally
			{
				if (webSocket != null)
					webSocket.Dispose();
			}
			*/
		}


		private List<WebSocketInfo> websocketInfos = new List<WebSocketInfo>();
		public record WebSocketInfo(ClientWebSocket ClientWebSocket, string Url, GoalToCallInfo GoalToCall, string WebSocketName, string ContentRecievedVariableName);
		public record WebSocketData(GoalToCallInfo GoalToCall, string Url, string Method, string Contract)
		{
			public Dictionary<string, object?> Parameters = new();
			public Signature? Signature = null;
		};
		public async Task SendToWebSocket(object data, Dictionary<string, object?>? headers = null, string webSocketName = "default")
		{
			throw new NotImplementedException();
		}

		public async Task SendToWebSocket(GoalToCallInfo goalToCall, Dictionary<string, object?>? parameters = null, string webSocketName = "default")
		{
			var webSocketInfo = websocketInfos.FirstOrDefault(p => p.WebSocketName == webSocketName);
			if (webSocketInfo == null)
			{
				throw new RuntimeException($"Websocket with name '{webSocketName}' does not exists");
			}

			string url = webSocketInfo.Url;
			string method = "Websocket";
			string[] contracts = ["C0"];

			var obj = new WebSocketData(goalToCall, url, method, null);
			obj.Parameters = parameters;

			var signature = await programFactory.GetProgram<Modules.IdentityModule.Program>(goalStep).Sign(obj);
			obj.Signature = signature;

			byte[] message = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj));

			await webSocketInfo.ClientWebSocket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, CancellationToken.None);

		}
		public async Task<WebSocketInfo> StartWebSocketConnection(string url, GoalToCallInfo goalToCall, string webSocketName = "default", string contentRecievedVariableName = "%content%")
		{
			if (string.IsNullOrEmpty(url))
			{
				throw new RuntimeException($"url cannot be empty");
			}

			if (goalToCall != null)
			{
				throw new RuntimeException($"goalToCall cannot be empty");
			}

			if (!url.StartsWith("ws://") && !url.StartsWith("wss://"))
			{
				throw new RuntimeException($"url must start with ws:// or wss://. You url is '{url}'");
			}

			if (websocketInfos.FirstOrDefault(p => p.WebSocketName == webSocketName) != null)
			{
				throw new RuntimeException($"Websocket with name '{webSocketName}' already exists");
			}

			ClientWebSocket client = new ClientWebSocket();
			await client.ConnectAsync(new Uri(url), CancellationToken.None);
			var webSocketInfo = new WebSocketInfo(client, url, goalToCall, webSocketName, contentRecievedVariableName);

			websocketInfos.Add(webSocketInfo);

			Console.WriteLine("Connected to the server");

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
			Task.Run(async () =>
			{
				byte[] buffer = new byte[1024];
				MemoryStream messageStream = new MemoryStream();
				while (true)
				{

					WebSocketReceiveResult result;
					do
					{
						result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
						messageStream.Write(buffer, 0, result.Count);
					}
					while (!result.EndOfMessage);

					messageStream.Seek(0, SeekOrigin.Begin);

					if (result.MessageType == WebSocketMessageType.Close)
					{
						await client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
						break;
					}
					else if (result.MessageType == WebSocketMessageType.Text)
					{
						StreamReader reader = new StreamReader(messageStream, Encoding.UTF8);
						string receivedMessage = await reader.ReadToEndAsync();
						reader.Dispose();

						var websocketData = JsonConvert.DeserializeObject<WebSocketData>(receivedMessage);
						if (websocketData == null || string.IsNullOrEmpty(websocketData.GoalToCall))
						{
							continue;
						}

						var signature = websocketData.Signature;
						var verifiedSignature = await programFactory.GetProgram<Modules.IdentityModule.Program>(goalStep).VerifySignature(signature);
						// todo: missing verifiedSignature.Error check
						if (verifiedSignature.Signature == null)
						{
							continue;
						}

						websocketData.GoalToCall.Parameters.AddOrReplace(ReservedKeywords.Identity, verifiedSignature.Signature.Identity);

						await pseudoRuntime.RunGoal(engine, context, fileSystem.RootDirectory, websocketData.GoalToCall);
					}
					messageStream.SetLength(0);
				}
				messageStream.Dispose();
			});

			return webSocketInfo;
		}


		private static string GetBoundary(MediaTypeHeaderValue contentType, int lengthLimit)
		{
			var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
			if (string.IsNullOrWhiteSpace(boundary))
			{
				throw new InvalidDataException("Missing content-type boundary.");
			}
			if (boundary.Length > lengthLimit)
			{
				throw new InvalidDataException(
					$"Multipart boundary length limit {lengthLimit} exceeded.");
			}
			return boundary;
		}



	}
}

