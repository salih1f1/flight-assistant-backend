using FlightAssistant.Application.DataSources;

namespace FlightAssistant.Application.Interfaces.DataSources
{
    /// <summary>Every outside dependency the application has. Registered as a singleton.</summary>
    public interface IDataSourceCatalog
    {
        IReadOnlyList<DataSourceDescriptor> Sources { get; }

        DataSourceDescriptor? TryGet(string key);
    }
}
