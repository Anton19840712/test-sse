using Serilog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using test_sse_next;

// Инициализация логирования
Log.Logger = new LoggerConfiguration()
	.WriteTo.Console()
	.CreateLogger();

try
{
	// Создание и запуск хоста
	var host = Host.CreateDefaultBuilder(args)
		.UseSerilog()
		.ConfigureServices((_, services) =>
		{
			services.AddHostedService<SseBackgroundService>();
		})
		.Build();

	await host.RunAsync();
}
catch (Exception ex)
{
	Log.Fatal(ex, "Приложение завершилось с ошибкой");
}
finally
{
	Log.CloseAndFlush();
}