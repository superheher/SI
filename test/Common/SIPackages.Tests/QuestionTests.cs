﻿using SIPackages.Core;
using System.Text;
using System.Xml;

namespace SIPackages.Tests;

internal sealed class QuestionTests
{
    [Test]
    public void Serialize_Deserialize_Ok()
    {
        var question = new Question
        {
            Script = new Script
            {
                Steps =
                {
                    new Step
                    {
                        Type = StepTypes.ShowContent,
                        Parameters =
                        {
                            [StepParameterNames.Content] = new StepParameter
                            {
                                Type = StepParameterTypes.Content,
                                ContentValue = new List<ContentItem>
                                {
                                    new ContentItem { Type = AtomTypes.Text, Value = "item text" }
                                }
                            }
                        }
                    }
                }
            },
            Parameters = new StepParameters
            {
                ["test"] = new StepParameter
                {
                    Type = StepParameterTypes.Group,
                    GroupValue = new StepParameters
                    {
                        ["inner"] = new StepParameter
                        {
                            Type = StepParameterTypes.Simple,
                            SimpleValue = "value"
                        }
                    }
                }
            }
        };

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb))
        {
            question.WriteXml(writer);
        }

        var result = sb.ToString();

        var xmlDocument = new XmlDocument();
        xmlDocument.LoadXml(result);

        var itemValue = xmlDocument["question"]?["script"]?["step"]?["param"]?["item"]?.InnerText;
        Assert.That(itemValue, Is.EqualTo("item text"));

        var paramValue = xmlDocument["question"]?["params"]?["param"]?.InnerText;
        Assert.That(paramValue, Is.EqualTo("value"));

        var newQuestion = new Question();

        using (var textReader = new StringReader(result))
        using (var reader = XmlReader.Create(textReader))
        {
            newQuestion.ReadXml(reader);
        }

        var newStep = newQuestion.Script?.Steps[0];

        Assert.That(newStep?.Type, Is.EqualTo(StepTypes.ShowContent));
        Assert.That(newStep?.Parameters[StepParameterNames.Content].Type, Is.EqualTo(StepParameterTypes.Content));
        Assert.That(newStep?.Parameters[StepParameterNames.Content].ContentValue?[0].Value, Is.EqualTo("item text"));

        var newParam = newQuestion.Parameters?["test"];

        Assert.That(newParam?.Type, Is.EqualTo(StepParameterTypes.Group));
        Assert.That(newParam?.GroupValue?["inner"].Type, Is.EqualTo(StepParameterTypes.Simple));
        Assert.That(newParam?.GroupValue?["inner"].SimpleValue, Is.EqualTo("value"));
    }
}
