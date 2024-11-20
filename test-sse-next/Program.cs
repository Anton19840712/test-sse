using System.Net;
using System.Text;
using Serilog;
using System.Collections.Concurrent;

class Program
{
	static async Task Main(string[] args)
	{
		// Инициализация логирования
		Log.Logger = new LoggerConfiguration()
			.WriteTo.Console()
			.CreateLogger();

		// Запуск SSE-сервера
		var sseServer = new SseBackgroundService();
		await sseServer.StartAsync(CancellationToken.None);

		// Ожидание нажатия клавиши для завершения работы
		Console.WriteLine("Press any key to stop the server...");
		Console.ReadKey();

		// Завершаем работу сервера
		await sseServer.StopAsync(CancellationToken.None);

		// Очищаем ресурсы логирования
		Log.CloseAndFlush();
	}
}

public class SseBackgroundService
{
	private readonly HttpListener _listener;
	private readonly ConcurrentBag<HttpListenerResponse> _clients;  // Изменено на ConcurrentBag
	private readonly List<string> _messages; // Список сообщений
	private Timer _messageTimer;  // Таймер для отправки сообщений каждую секунду
	private int _counter;

	public SseBackgroundService()
	{
		_listener = new HttpListener();
		_listener.Prefixes.Add("http://*:52799/sse/");
		_clients = new ConcurrentBag<HttpListenerResponse>();  // Инициализируем ConcurrentBag для клиентов
		_messages = new List<string>();  // Список для хранения сообщений
		_counter = 0; // Инициализация счётчика
	}

	public async Task StartAsync(CancellationToken stoppingToken)
	{
		_listener.Start();
		Log.Information("SSE Server started at http://*:52799/sse/");

		// Таймер для периодической отправки сообщений
		_messageTimer = new Timer(SendGeneratedMessages, stoppingToken, 0, 1000); // каждую секунду

		try
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				var context = await _listener.GetContextAsync();

				// Обрабатываем запросы в отдельном потоке
				_ = Task.Run(() => HandleRequestAsync(context, stoppingToken), stoppingToken);
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Exception in SSE Server: {ex.Message}");
		}
		finally
		{
			_listener.Stop();
			_listener.Close();
			_messageTimer?.Dispose();  // Закрываем таймер
		}
	}

	private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken stoppingToken)
	{
		try
		{
			if (context.Request.HttpMethod == "POST") // Обработка POST-запросов
			{
				var requestBody = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
				Log.Information($"Received message: {requestBody}");

				// Добавляем сообщение в список
				_messages.Add(requestBody);

				// Отправляем сообщение всем подключённым клиентам
				foreach (var client in _clients)
				{
					SendSseMessageAsync(client, requestBody, stoppingToken);
				}

				// Ответ на запрос
				var response = context.Response;
				response.StatusCode = 200;
				response.Close();
			}
			else // Обработка SSE-соединений
			{
				var response = context.Response;

				response.Headers.Add("Access-Control-Allow-Origin", "*");
				response.Headers.Add("Content-Type", "text/event-stream");
				response.Headers.Add("Cache-Control", "no-cache");
				response.Headers.Add("Connection", "keep-alive");

				Log.Information("New SSE connection established");

				// Добавляем нового клиента в список
				_clients.Add(response);

				// Отправляем все накопленные сообщения новому клиенту
				foreach (var message in _messages)
				{
					await SendSseMessageAsync(response, message, stoppingToken);
				}

				// Ожидаем новые сообщения и отправляем их
				while (!stoppingToken.IsCancellationRequested)
				{
					await Task.Delay(1000, stoppingToken); // Задержка в 1 секунду
				}

				// Удаляем клиента из списка при закрытии соединения
				_clients.TryTake(out _);  // Попытка безопасно удалить клиента

				response.Close();
				Log.Information("SSE connection closed");
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Error handling request: {ex.Message}");
		}
	}

	private async Task SendSseMessageAsync(
		HttpListenerResponse response,
		string message,
		CancellationToken stoppingToken)
	{
		// Форматируем сообщение для SSE
		var formattedMessage = $"data: {message}\n\n";

		// Отправляем сообщение клиенту
		var buffer = Encoding.UTF8.GetBytes(formattedMessage);
		await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, stoppingToken);
		await response.OutputStream.FlushAsync(stoppingToken);
	}

	private void SendGeneratedMessages(object state)
	{
		var stoppingToken = (CancellationToken)state;

		// Создаем список клиентов для которых необходимо удалить соединение
		var disconnectedClients = new List<HttpListenerResponse>();

		foreach (var client in _clients)
		{
			try
			{
				var message = $"Generated message #{_counter++} at {DateTime.Now}";
				_messages.Add(message); // Сохраняем сообщение в список

				// Отправляем сообщение, если клиент всё ещё подключен
				SendSseMessageAsync(client, message, stoppingToken).Wait();
			}
			catch (Exception ex)
			{
				Log.Error($"Error sending message to client: {ex.Message}");

				// Если возникла ошибка, предполагаем, что клиент отключен
				disconnectedClients.Add(client);
			}
		}

		// Удаляем отключившихся клиентов из списка
		foreach (var disconnectedClient in disconnectedClients)
		{
			_clients.TryTake(out _); // Без блокировки, безопасное удаление
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		_listener.Stop();
		await Task.CompletedTask;
	}
}
