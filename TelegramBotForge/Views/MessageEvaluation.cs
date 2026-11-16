using System.Text.RegularExpressions;
using TelegramBotForge.Media;

namespace TelegramBotForge.Views;

internal sealed record MessagePayload(BotMessage Message, MediaFile[] Attachments);

internal static class MessageEvaluation
{
    private static readonly Regex References = new("^tg://(photo|video|audio|document)\\?id=([a-zA-Z0-9_-]+)$", RegexOptions.Compiled);

    internal static MessagePayload Evaluate(Message description)
    {
        var attachments = new List<MediaFile>();
        var keyboard = new MessageBuilder(description.Origin, description.Rich, description.ConfigurePagination);
        var html = Render(description.Content);
        var validated = MessageValidation.Validate(html, description.Rich, description.Origin);
        foreach (var element in validated.Descendants().Where(element => element.Name.LocalName is "img" or "video" or "audio" or "tg-document"))
        {
            var source = (string?)element.Attribute("src") ?? "";
            if (element.Name.LocalName == "img" && source.StartsWith("tg://emoji?id=", StringComparison.Ordinal))
                continue;
            if (Uri.TryCreate(source, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                continue;
            var reference = References.Match(source);
            var id = reference.Groups[2].Value;
            if (!reference.Success || !int.TryParse(id.AsSpan(id.StartsWith("m", StringComparison.Ordinal) ? 1 : 0), out var index)
                || id != $"m{index}" || index < 0 || index >= attachments.Count
                || element.Name.LocalName != (attachments[index].Type == MediaType.Photo ? "img" : "video")
                || reference.Groups[1].Value != (attachments[index].Type == MediaType.Photo ? "photo" : "video"))
                throw Problem($"unregistered or mismatched media reference '{source}'. Use Photo, Video or Slideshow.");
        }
        var mediaLimit = description.Rich ? 50 : 10;
        if (attachments.Count > mediaLimit)
            throw Problem($"a {(description.Rich ? "rich" : "standard")} message supports at most {mediaLimit} photo/video attachments.");
        if ((string.IsNullOrWhiteSpace(html) || !description.Rich && string.IsNullOrWhiteSpace(validated.Value)) && attachments.Count == 0)
            throw Problem("the message has no content.");
        keyboard.Text("validation");
        var built = keyboard.Build();
        return new MessagePayload(built with { Text = !description.Rich && string.IsNullOrWhiteSpace(validated.Value) ? "\u200B" : html }, attachments.ToArray());

        string Render(IEnumerable<Content> content) => string.Join(description.Rich ? "\n" : "\n\n", content.Select(part => part switch
        {
            InlineContent { Heading: false, Text.IsPreformatted: true } preformatted => preformatted.Text.Html,
            InlineContent inline => description.Rich
                ? inline.Heading ? $"<h2>{inline.Text.Html}</h2>" : $"<p>{inline.Text.Html}</p>"
                : inline.Heading ? $"<b>{inline.Text.Html}</b>" : inline.Text.Html,
            SpaceContent => description.Rich ? "<br/>" : "\n",
            RawContent raw => raw.Html,
            TableContent table => description.Rich
                ? "<table compact>" + string.Concat(table.Rows.Select(row => "<tr>" + string.Concat(row.Select(cell => $"<td>{cell.Html}</td>")) + "</tr>")) + "</table>"
                : string.Join("\n", table.Rows.Select(row => row.Length == 2
                    ? $"<b>{row[0].Html}</b>: {row[1].Html}" : string.Join(" | ", row.Select(cell => cell.Html)))),
            DetailsContent details => description.Rich
                ? $"<details><summary>{details.Summary.Html}</summary>{Render(details.Body)}</details>"
                : $"<b>{details.Summary.Html}</b>\n{Render(details.Body)}",
            ComponentContent component => Render(component.Body),
            ButtonsContent buttons => Row(buttons.Buttons),
            MediaContent media => Media(media.Media),
            ReplyContent reply => Reply(reply),
            RemoveKeyboardContent => Remove(),
            RequestContent request => Request(request),
            _ => throw Problem($"unsupported content {part.GetType().Name}.")
        }).Where(part => part.Length != 0));

        string Row(BotButton[] buttons)
        {
            keyboard.Row(buttons);
            return !description.Rich || buttons.Length == 0 ? "" : "<tg-button-row>" + string.Concat(buttons.Select(MessageBuilder.ToRichButton)) + "</tg-button-row>";
        }
        string Media(MediaFile[] media)
        {
            var references = new List<string>();
            foreach (var file in media)
            {
                if (file.Type is not (MediaType.Photo or MediaType.Video))
                    throw Problem($"{file.Type} is unsupported in structured media; use photos or videos.");
                var id = "m" + attachments.Count;
                attachments.Add(file);
                references.Add(file.Type == MediaType.Photo ? $"<img src=\"tg://photo?id={id}\"/>" : $"<video src=\"tg://video?id={id}\"/>");
            }
            return description.Rich && references.Count != 0 ? "<tg-slideshow>" + string.Concat(references) + "</tg-slideshow>" : "";
        }
        string Reply(ReplyContent reply) { keyboard.ReplyButtons(reply.Labels, reply.PerRow); return ""; }
        string Remove() { keyboard.RemoveKeyboard(); return ""; }
        string Request(RequestContent request)
        {
            if (request.Location)
                keyboard.RequestLocation(request.Label);
            else
                keyboard.RequestContact(request.Label);
            return "";
        }
        InvalidOperationException Problem(string problem) => new($"View {description.Origin}: {problem}");
    }
}
