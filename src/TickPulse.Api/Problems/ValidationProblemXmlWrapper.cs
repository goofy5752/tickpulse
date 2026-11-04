using System.Xml;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters.Xml;

namespace TickPulse.Api.Problems;

/// <summary>
/// Writes a validation problem like MVC's <see cref="ProblemDetailsWrapper"/>, with the per-parameter messages under
/// an <c>errors</c> element that mirrors the JSON member: <c>&lt;errors&gt;&lt;n&gt;message&lt;/n&gt;&lt;/errors&gt;</c>,
/// one element per message, named after the parameter. (MVC's own wrapper calls it <c>MVC-Errors</c>.)
/// </summary>
[XmlRoot("problem", Namespace = "urn:ietf:rfc:7807")]
public sealed class ValidationProblemXmlWrapper : ProblemDetailsWrapper
{
    /// <summary>The element name used for messages that are not about a named parameter.</summary>
    public const string UnnamedKey = "request";

    private readonly ValidationProblemDetails? _problem;

    /// <summary>Required by <see cref="System.Runtime.Serialization.DataContractSerializer"/>; not used for writing.</summary>
    public ValidationProblemXmlWrapper()
    {
    }

    public ValidationProblemXmlWrapper(ValidationProblemDetails problem)
        : base(problem) => _problem = problem;

    public override void WriteXml(XmlWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        base.WriteXml(writer);
        if (_problem is not { Errors.Count: > 0 })
        {
            return;
        }

        writer.WriteStartElement("errors");
        foreach (var (key, messages) in _problem.Errors)
        {
            var name = XmlConvert.EncodeLocalName(string.IsNullOrEmpty(key) ? UnnamedKey : key);
            foreach (var message in messages)
            {
                writer.WriteElementString(name, message);
            }
        }

        writer.WriteEndElement();
    }
}
