using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayProtocolPrimitive
{
    public static Prompt Clone(Prompt prompt) =>
        new()
        {
            Name = prompt.Name,
            Title = prompt.Title,
            Description = prompt.Description,
            Arguments = prompt.Arguments?.Select(ClonePromptArgument).ToList(),
            Icons = CloneIcons(prompt.Icons),
            Meta = McpGatewayProtocolTool.CloneMeta(prompt.Meta),
        };

    public static Resource Clone(Resource resource) =>
        new()
        {
            Name = resource.Name,
            Title = resource.Title,
            Uri = resource.Uri,
            Description = resource.Description,
            MimeType = resource.MimeType,
            Annotations = CloneAnnotations(resource.Annotations),
            Size = resource.Size,
            Icons = CloneIcons(resource.Icons),
            Meta = McpGatewayProtocolTool.CloneMeta(resource.Meta),
        };

    public static ResourceTemplate Clone(ResourceTemplate template) =>
        new()
        {
            Name = template.Name,
            Title = template.Title,
            UriTemplate = template.UriTemplate,
            Description = template.Description,
            MimeType = template.MimeType,
            Annotations = CloneAnnotations(template.Annotations),
            Icons = CloneIcons(template.Icons),
            Meta = McpGatewayProtocolTool.CloneMeta(template.Meta),
        };

    public static PromptArgument ClonePromptArgument(PromptArgument argument) =>
        new()
        {
            Name = argument.Name,
            Title = argument.Title,
            Description = argument.Description,
            Required = argument.Required,
        };

    public static IList<Icon>? CloneIcons(IList<Icon>? icons) =>
        icons?.Select(static icon => new Icon
        {
            Source = icon.Source,
            MimeType = icon.MimeType,
            Sizes = icon.Sizes?.ToArray(),
            Theme = icon.Theme,
        }).ToList();

    private static Annotations? CloneAnnotations(Annotations? annotations) =>
        annotations is null
            ? null
            : new Annotations
            {
                Audience = annotations.Audience?.ToArray(),
                Priority = annotations.Priority,
                LastModified = annotations.LastModified,
            };
}
