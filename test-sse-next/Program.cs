﻿using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Serilog;
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
	private readonly List<HttpListenerResponse> _clients;
	private readonly List<string> _messages; // Список сообщений
	private Timer _messageTimer;  // Таймер для отправки сообщений каждую секунду
	private int _counter;
	private const string MessageLogFilePath = "messages.log"; // Путь к файлу для хранения сообщений

	public SseBackgroundService()
	{
		_listener = new HttpListener();
		_listener.Prefixes.Add("http://*:52799/sse/");
		_clients = new List<HttpListenerResponse>();  // Инициализируем список клиентов
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

				// Записываем сообщение в файл
				SaveMessageToFile(requestBody);

				// Отправляем сообщение всем подключённым клиентам
				lock (_clients)
				{
					foreach (var client in _clients)
					{
						SendSseMessageAsync(client, requestBody, stoppingToken);
					}
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
				lock (_clients)
				{
					_clients.Add(response);
				}

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
				lock (_clients)
				{
					_clients.Remove(response);
				}

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
		lock (_clients)
		{
			foreach (var client in _clients)
			{
				var message = $"Generated message #{_counter++} at {DateTime.Now}";
				_messages.Add(message); // Сохраняем сообщение в список
				SaveMessageToFile(message); // Записываем сообщение в файл
				SendSseMessageAsync(client, message, stoppingToken).Wait();
			}
		}
	}

	// Сохраняем сообщение в файл
	private void SaveMessageToFile(string message)
	{
		try
		{
			File.AppendAllText(MessageLogFilePath, $"{message}\n");
		}
		catch (Exception ex)
		{
			Log.Error($"Error saving message to file: {ex.Message}");
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		_listener.Stop();
		await Task.CompletedTask;
	}
}

public class MessageBuffer
{
	private readonly ConcurrentQueue<string> _messages = new();
	private readonly int _maxMessages;

	public MessageBuffer(int maxMessages)
	{
		_maxMessages = maxMessages;
	}

	public void AddMessage(string message)
	{
		_messages.Enqueue(message);

		// Удаляем старые сообщения, если превышен лимит
		while (_messages.Count > _maxMessages)
		{
			_messages.TryDequeue(out _);
		}
	}

	public List<string> GetAllMessages()
	{
		return _messages.ToList();
	}
}