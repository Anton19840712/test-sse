using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.IO;

public class Program
{
	public static async Task Main(string[] args)
	{
		// Настройка параметров сервера
		var httpHostRequest = new HttpHostRequest
		{
			ProcessId = new List<string> { "process1", "process2" }, // Пример значений
			Prefixes = new string[] { "http://localhost:8080/" } // Префикс для HTTP Listener
		};

		// Создаем и запускаем сервер
		var server = new HttpServerConnect(httpHostRequest);

		Console.WriteLine("Сервер запущен. Для остановки нажмите Enter...");
		Console.ReadLine();

		// Закрываем сервер по завершению работы
		await server.DisposeAsync();
	}
}

public class HttpHostRequest
{
	public List<string> ProcessId { get; set; } = new();
	public string[] Prefixes { get; set; } = Array.Empty<string>();
}

public class HttpServerConnect : IDisposable, IAsyncDisposable
{
	public HttpListener HttpListener { get; } = new();
	public List<string> ProcessId { get; }
	private int messageId;
	private readonly ConcurrentQueue<string> messageQueue = new();
	private readonly ConcurrentBag<HttpListenerContext> connectedClients = new();

	public HttpServerConnect(HttpHostRequest request)
	{
		ProcessId = request.ProcessId;

		foreach (string prefix in request.Prefixes)
		{
			HttpListener.Prefixes.Add(prefix);
		}

		HttpListener.Start();
		// Запускаем обработчики
		Task.Run(AcceptConnections);
		Task.Run(HeartBeat);
	}

	private async Task AcceptConnections()
	{
		while (HttpListener.IsListening)
		{
			try
			{
				var context = await HttpListener.GetContextAsync();
				connectedClients.Add(context);

				// Устанавливаем заголовки для SSE только один раз
				var response = context.Response;
				response.Headers["Access-Control-Allow-Origin"] = "*"; // Допускаем кросс-доменные запросы
				response.ContentType = "text/event-stream"; // Устанавливаем формат для SSE
				response.Headers["Cache-Control"] = "no-cache"; // Отключаем кеширование
				response.Headers["Connection"] = "keep-alive"; // Соединение остается активным

				Console.WriteLine("Client connected");

				EnqueueMessage("Client connected");
			}
			catch (HttpListenerException ex) when (ex.ErrorCode == 995) // ERROR_OPERATION_ABORTED
			{
				break; // Сервер остановлен
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка обработки подключения: {ex.Message}");
			}
		}
	}

	private async Task HeartBeat()
	{
		while (HttpListener.IsListening)
		{
			await Task.Delay(10000); // Отправляем heartbeat каждые 10 секунд
			EnqueueMessage("heart beat");
			await SendPendingMessages();
		}
	}

	public void EnqueueMessage(string data)
	{
		messageQueue.Enqueue(data);
	}

	private async Task SendPendingMessages()
	{
		while (messageQueue.TryDequeue(out string? message))
		{
			await SendMessageToAllClients(message);
		}
	}

	private async Task SendMessageToAllClients(string data)
	{
		string formattedMessage = GetMessageForm(data);
		byte[] buffer = Encoding.UTF8.GetBytes(formattedMessage);

		foreach (var client in connectedClients)
		{
			try
			{
				client.Response.KeepAlive = true;

				using Stream output = client.Response.OutputStream;
				await output.WriteAsync(buffer);
				await output.FlushAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка отправки клиенту: {ex.Message}");
			}
		}

		messageId++;
	}

	private string GetMessageForm(string data) => $"data: {data}\nid:{messageId} \n\n";

	public async Task DisposeAsync()
	{
		EnqueueMessage("The server closes the connection.");
		await SendPendingMessages();

		HttpListener.Stop();
		HttpListener.Close();
	}

	public void Dispose()
	{
		DisposeAsync().GetAwaiter().GetResult();
	}

	ValueTask IAsyncDisposable.DisposeAsync()
	{
		throw new NotImplementedException();
	}
}
