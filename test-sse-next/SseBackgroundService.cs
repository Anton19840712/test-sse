using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace test_sse_next
{
	public class SseBackgroundService : BackgroundService
	{
		private readonly HttpListener _listener;
		private readonly ConcurrentBag<HttpListenerResponse> _clients;
		private readonly List<string> _messages;
		private Timer _messageTimer;
		private int _counter;

		public SseBackgroundService()
		{
			_listener = new HttpListener();
			_listener.Prefixes.Add("http://*:52799/sse/");
			_clients = new ConcurrentBag<HttpListenerResponse>();
			_messages = new List<string>();
			_counter = 0;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			_listener.Start();
			Log.Information("SSE Server started at http://*:52799/sse/");

			// Таймер для отправки автоматических сообщений
			_messageTimer = new Timer(SendGeneratedMessages, stoppingToken, 0, 1000);

			try
			{
				while (!stoppingToken.IsCancellationRequested)
				{
					var context = await _listener.GetContextAsync();

					// Обработка запросов в отдельном потоке
					_ = Task.Run(() => HandleRequestAsync(context, stoppingToken), stoppingToken);
				}
			}
			catch (HttpListenerException ex) when (ex.ErrorCode == 995) // Операция отменена
			{
				Log.Warning("Сервер остановлен.");
			}
			catch (Exception ex)
			{
				Log.Error($"Ошибка в SSE сервере: {ex.Message}");
			}
			finally
			{
				await StopAsync(CancellationToken.None);
			}
		}

		private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken stoppingToken)
		{
			try
			{
				if (context.Request.HttpMethod == "POST")
				{
					var requestBody = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
					Log.Information($"Получено сообщение: {requestBody}");

					_messages.Add(requestBody);

					foreach (var client in _clients)
					{
						await SendSseMessageAsync(client, requestBody, stoppingToken);
					}

					context.Response.StatusCode = 200;
					context.Response.Close();
				}
				else if (context.Request.HttpMethod == "GET")
				{
					var response = context.Response;

					response.Headers.Add("Access-Control-Allow-Origin", "*");
					response.Headers.Add("Content-Type", "text/event-stream");
					response.Headers.Add("Cache-Control", "no-cache");
					response.Headers.Add("Connection", "keep-alive");

					Log.Information("Новое SSE соединение установлено.");
					_clients.Add(response);

					foreach (var message in _messages)
					{
						await SendSseMessageAsync(response, message, stoppingToken);
					}

					while (!stoppingToken.IsCancellationRequested)
					{
						await Task.Delay(1000, stoppingToken);
					}

					response.Close();
					Log.Information("SSE соединение закрыто.");
				}
			}
			catch (Exception ex)
			{
				Log.Error($"Ошибка обработки запроса: {ex.Message}");
			}
		}

		private async Task SendSseMessageAsync(
			HttpListenerResponse response,
			string message,
			CancellationToken stoppingToken)
		{
			try
			{
				var formattedMessage = $"data: {message}\n\n";
				var buffer = Encoding.UTF8.GetBytes(formattedMessage);
				await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, stoppingToken);
				await response.OutputStream.FlushAsync(stoppingToken);
			}
			catch (Exception ex)
			{
				Log.Error($"Ошибка отправки сообщения клиенту: {ex.Message}");
			}
		}

		private void SendGeneratedMessages(object state)
		{
			var stoppingToken = (CancellationToken)state;

			foreach (var client in _clients)
			{
				try
				{
					var message = $"Сгенерированное сообщение #{_counter++} в {DateTime.Now}";
					_messages.Add(message);
					SendSseMessageAsync(client, message, stoppingToken).Wait();
				}
				catch (Exception ex)
				{
					Log.Warning($"Ошибка при отправке сообщения: {ex.Message}");
				}
			}
		}

		public override Task StopAsync(CancellationToken cancellationToken)
		{
			_listener.Stop();
			_listener.Close();
			_messageTimer?.Dispose();
			Log.Information("SSE сервер остановлен.");
			return Task.CompletedTask;
		}
	}
}
