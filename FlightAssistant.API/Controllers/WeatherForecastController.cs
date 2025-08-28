using FlightAssistant.Application.Interfaces.Caching;
using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController : ControllerBase
    {
        private static readonly string[] Summaries = new[]
        {
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        };

        private readonly ILogger<WeatherForecastController> _logger;
        private readonly ICacheService cacheService;

        public WeatherForecastController(
            ILogger<WeatherForecastController> logger,
            ICacheService cacheService
            )
        {
            _logger = logger;
            this.cacheService = cacheService;
        }

        [HttpGet(Name = "GetWeatherForecast")]
        public async Task<IEnumerable<WeatherForecast>> Get()
        {
            await cacheService.SetAsync("test", "value");

            var tst = cacheService.GetAsync<string>("test").GetAwaiter().GetResult();

            await cacheService.GetOrSetAsync("test2", async (ct) =>
            {
                await Task.Delay(1000);
                return "value from factory";
            });

            return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
        }
    }
}
