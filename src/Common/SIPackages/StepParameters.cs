﻿using SIPackages.Helpers;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace SIPackages;

/// <summary>
/// Defines a collection of step parameters.
/// </summary>
public sealed class StepParameters : Dictionary<string, StepParameter>, IEquatable<StepParameters>, IXmlSerializable
{
    private readonly string _ownerTagName;

    /// <summary>
    /// Initializes a new instance of <see cref="StepParameters" /> class.
    /// </summary>
    /// <param name="ownerTagName">Parameters owner tag name.</param>
    public StepParameters(string ownerTagName = "params") => _ownerTagName = ownerTagName;

    /// <inheritdoc />
    public bool Equals(StepParameters? other) => other is not null && this.SequenceEqual(other);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as StepParameters);

    /// <inheritdoc />
    public override int GetHashCode() => this.GetCollectionHashCode();

    /// <inheritdoc />
    public XmlSchema? GetSchema() => null;

    /// <inheritdoc />
    public void ReadXml(XmlReader reader)
    {
        var read = true;

        while (!read || reader.Read())
        {
            read = true;

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    switch (reader.LocalName)
                    {
                        case "param":
                            var name = "";

                            if (reader.MoveToAttribute("name"))
                            {
                                name = reader.Value;
                            }

                            var parameter = new StepParameter();
                            parameter.ReadXml(reader);
                            this[name] = parameter;
                            read = false;
                            break;
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (reader.LocalName == _ownerTagName)
                    {
                        reader.Read();
                        return;
                    }
                    break;
            }
        }
    }

    /// <inheritdoc />
    public void WriteXml(XmlWriter writer)
    {
        foreach (var item in this)
        {
            writer.WriteStartElement("param");
            writer.WriteAttributeString("name", item.Key);
            item.Value.WriteXml(writer);
            writer.WriteEndElement();
        }
    }
}
