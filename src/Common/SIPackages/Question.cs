﻿using SIPackages.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml;

namespace SIPackages;

/// <summary>
/// Defines a game question.
/// </summary>
public sealed class Question : InfoOwner, IEquatable<Question>
{
    /// <summary>
    /// Question price that means empty question.
    /// </summary>
    public const int InvalidPrice = -1;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private int _price;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string? _typeName;

    /// <summary>
    /// Question base price.
    /// </summary>
    [DefaultValue(0)]
    public int Price
    {
        get => _price;
        set { var oldValue = _price; if (oldValue != value) { _price = value; OnPropertyChanged(oldValue); } }
    }

    /// <summary>
    /// Question type.
    /// </summary>
    [DefaultValue(typeof(QuestionType), QuestionTypes.Simple)]
    public QuestionType Type { get; } = new();

    /// <summary>
    /// Question base price.
    /// </summary>
    public string? TypeName
    {
        get => _typeName;
        set { var oldValue = _typeName; if (oldValue != value) { _typeName = value; OnPropertyChanged(oldValue); } }
    }

    /// <summary>
    /// Question scenario.
    /// </summary>
    public Scenario Scenario { get; } = new();

    /// <summary>
    /// Question script.
    /// </summary>
    public Script? Script { get; set; }

    /// <summary>
    /// Question parameters.
    /// </summary>
    public StepParameters? Parameters { get; set; }

    /// <summary>
    /// Right answers.
    /// </summary>
    public Answers Right { get; } = new();

    /// <summary>
    /// Wrong answers.
    /// </summary>
    public Answers Wrong { get; } = new();

    /// <inheritdoc />
    public override string ToString() => $"{_price}: {Scenario} ({(Right.Count > 0 ? Right[0] : "")})";

    /// <summary>
    /// Question name (not used).
    /// </summary>
    [DefaultValue("")]
    public override string Name => "";

    /// <inheritdoc />
    public override bool Contains(string value) =>
        base.Contains(value) ||
        Type.Contains(value) ||
        Scenario.ContainsQuery(value) ||
        Right.ContainsQuery(value) ||
        Wrong.ContainsQuery(value);

    /// <inheritdoc />
    public override IEnumerable<SearchData> Search(string value)
    {
        foreach (var item in base.Search(value))
        {
            yield return item;
        }

        foreach (var item in Type.Search(value))
        {
            yield return item;
        }

        foreach (var item in Scenario.Search(value))
        {
            yield return item;
        }

        foreach (var item in Right.Search(value))
        {
            item.Kind = ResultKind.Right;
            yield return item;
        }

        foreach (var item in Wrong.Search(value))
        {
            item.Kind = ResultKind.Wrong;
            yield return item;
        }
    }

    /// <inheritdoc />
    public override void ReadXml(XmlReader reader)
    {
        var priceStr = reader.GetAttribute("price");
        _ = int.TryParse(priceStr, out _price);

        if (reader.MoveToAttribute("type"))
        {
            _typeName = reader.Value;
        }

        var right = true;
        var read = true;

        while (!read || reader.Read())
        {
            read = true;

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    switch (reader.LocalName)
                    {
                        case "info":
                            base.ReadXml(reader);
                            read = false;
                            break;

                        case "type":
                            Type.Name = reader.GetAttribute("name");
                            break;

                        case "param":
                            var param = new QuestionTypeParam
                            {
                                Name = reader.GetAttribute("name"),
                                Value = reader.ReadElementContentAsString()
                            };

                            Type.Params.Add(param);
                            read = false;
                            break;

                        case "script":
                            Script = new();
                            Script.ReadXml(reader);
                            read = false;
                            break;

                        case "params":
                            Parameters = new();
                            Parameters.ReadXml(reader);
                            read = false;
                            break;

                        case "atom":
                            var atom = new Atom();

                            if (reader.MoveToAttribute("time"))
                            {
                                if (int.TryParse(reader.Value, out int time))
                                {
                                    atom.AtomTime = time;
                                }
                            }

                            if (reader.MoveToAttribute("type"))
                            {
                                atom.Type = reader.Value;
                            }

                            reader.MoveToElement();
                            atom.Text = reader.ReadElementContentAsString();

                            Scenario.Add(atom);
                            read = false;
                            break;

                        case "right":
                            right = true;
                            break;

                        case "wrong":
                            right = false;
                            break;

                        case "answer":
                            var answer = reader.ReadElementContentAsString();

                            if (right)
                            {
                                Right.Add(answer);
                            }
                            else
                            {
                                Wrong.Add(answer);
                            }

                            read = false;
                            break;
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (reader.LocalName == "question")
                    {
                        reader.Read();
                        return;
                    }
                    break;
            }
        }
    }

    /// <inheritdoc />
    public override void WriteXml(XmlWriter writer)
    {
        writer.WriteStartElement("question");
        writer.WriteAttributeString("price", _price.ToString());

        if (_typeName != null && _typeName != QuestionTypes.Simple)
        {
            writer.WriteAttributeString("type", _typeName.ToString());
        }

        base.WriteXml(writer);

        if (Type.Name != QuestionTypes.Simple)
        {
            writer.WriteStartElement("type");
            writer.WriteAttributeString("name", Type.Name);

            foreach (var item in Type.Params)
            {
                writer.WriteStartElement("param");
                writer.WriteAttributeString("name", item.Name);
                writer.WriteValue(item.Value);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteStartElement("scenario");

        foreach (var atom in Scenario)
        {
            writer.WriteStartElement("atom");

            if (atom.AtomTime != 0)
            {
                writer.WriteAttributeString("time", atom.AtomTime.ToString());
            }

            if (atom.Type != AtomTypes.Text)
            {
                writer.WriteAttributeString("type", atom.Type);
            }

            writer.WriteValue(atom.Text);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        if (Script != null)
        {
            writer.WriteStartElement("script");
            Script.WriteXml(writer);
            writer.WriteEndElement();
        }

        if (Parameters != null)
        {
            writer.WriteStartElement("params");
            Parameters.WriteXml(writer);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("right");

        foreach (var item in Right)
        {
            writer.WriteElementString("answer", item);
        }

        writer.WriteEndElement();

        if (Wrong.Any())
        {
            writer.WriteStartElement("wrong");

            foreach (var item in Wrong)
            {
                writer.WriteElementString("answer", item);
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Creates a copy of this question.
    /// </summary>
    public Question Clone()
    {
        var question = new Question { _price = _price };
        question.Type.Name = Type.Name;

        question.SetInfoFromOwner(this);

        foreach (var atom in Scenario)
        {
            question.Scenario.Add(new Atom { AtomTime = atom.AtomTime, Text = atom.Text, Type = atom.Type });
        }

        question.Right.Clear();

        question.Right.AddRange(Right);
        question.Wrong.AddRange(Wrong);

        foreach (var par in Type.Params)
        {
            question.Type[par.Name] = par.Value;
        }

        return question;
    }

    /// <summary>
    /// Gets question right answers.
    /// </summary>
    [Obsolete("Use Right property")]
    public IList<string> GetRightAnswers() => Right;

    /// <inheritdoc />
    public bool Equals(Question? other) =>
        other is not null
        && Price.Equals(other.Price)
        && Type.Equals(other.Type)
        && Scenario.Equals(other.Scenario)
        && Equals(Script, other.Script)
        && Equals(Parameters, other.Parameters)
        && Right.Equals(other.Right)
        && Wrong.Equals(other.Wrong);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Question);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Price, Type, Scenario, Script, Parameters, Right, Wrong);
}
