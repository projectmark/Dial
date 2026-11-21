using Microsoft.Extensions.DependencyInjection;

namespace Chassis.Core.Modules;

/// <summary>
/// Extension contract for mounting domain-specific features into a Chassis host shell.
/// </summary>
public interface IChassisModule
{
    /// <summary>Unique identifier of the module.</summary>
    string ModuleId { get; }

    /// <summary>Registers module services into the DI container before host construction.</summary>
    void RegisterServices(IServiceCollection services);

    /// <summary>Initializes module integrations (navigation, KB, settings categories) once services are built.</summary>
    void Initialize(IServiceProvider services);
}
