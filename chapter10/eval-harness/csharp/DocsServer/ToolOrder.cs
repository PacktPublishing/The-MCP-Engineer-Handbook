using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using System.Reflection;

// The C# SDK lists tools in hash order: stable across runs, but not the order they are
// declared in. Both hosts of DocsTools (this project's stdio entry point and the eval
// harness's in-process server) add this filter so tools/list follows declaration order,
// which is also the order the model sees the tools in.
public static class ToolOrder
{
    public static void ListInDeclarationOrder<TTools>(IMcpRequestFilterBuilder filters)
    {
        var order = typeof(TTools).GetMethods()
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(method => method.MetadataToken) // metadata tokens follow declaration order
            .Select((method, index) => (Name: method.GetCustomAttribute<McpServerToolAttribute>()!.Name!, index))
            .ToDictionary(entry => entry.Name, entry => entry.index);

        filters.AddListToolsFilter(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            result.Tools = [.. result.Tools.OrderBy(t => order.GetValueOrDefault(t.Name, int.MaxValue))];
            return result;
        });
    }
}
