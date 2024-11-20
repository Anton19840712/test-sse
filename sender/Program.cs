using Serilog;
using System.Text;

class Program
{
	static async Task Main(string[] args)
	{
		// Инициализация логирования
		Log.Logger = new LoggerConfiguration()
			.WriteTo.Console()
			.CreateLogger();

		// Создание HTTP клиента один раз
		using var client = new HttpClient();

		// Адрес вашего сервера
		string serverUrl = "http://localhost:52799/sse/";

		Console.WriteLine("Enter a message to send to the server (type 'exit' to quit):");

		// Цикл для ввода сообщений
		while (true)
		{
			// Ввод сообщения
			string message = Console.ReadLine();

			// Если пользователь вводит 'exit', завершаем работу
			if (message?.ToLower() == "exit")
			{
				break;
			}

			// Отправка введенного сообщения
			await SendMessageAsync(client, serverUrl, message);
		}

		// Завершаем работу
		Log.CloseAndFlush();
	}

	// Метод для отправки сообщения на сервер
	static async Task SendMessageAsync(HttpClient client, string url, string message)
	{
		try
		{
			// Формируем контент для отправки
			var content = new StringContent(message, Encoding.UTF8, "application/json");

			// Отправка POST запроса
			var response = await client.PostAsync(url, content);

			// Проверка ответа от сервера
			if (response.IsSuccessStatusCode)
			{
				Log.Information($"Message sent successfully: {message}");
			}
			else
			{
				Log.Warning($"Failed to send message: {response.StatusCode}");
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Error sending message: {ex.Message}");
		}
	}
}
