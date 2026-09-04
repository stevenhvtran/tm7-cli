using System.Runtime.Serialization;

namespace Tm7.Cli.Model;

[DataContract(Name = "Border", Namespace = "http://schemas.datacontract.org/2004/07/ThreatModeling.Model.Abstracts")]
public abstract class SerializableBorder : SerializableTaggable
{
    [DataMember(Name = "Height")]
    public int Height { get; private set; }

    [DataMember(Name = "Left")]
    public int Left { get; private set; }

    [DataMember(Name = "StrokeDashArray")]
    public string StrokeDashArray { get; private set; }

    [DataMember(Name = "StrokeThickness")]
    public double StrokeThickness { get; private set; }

    [DataMember(Name = "Top")]
    public int Top { get; private set; }

    [DataMember(Name = "Width")]
    public int Width { get; private set; }

    protected SerializableBorder(Guid guid, string typeId, string genericTypeId,
        IEnumerable<SerializableDisplayAttribute> properties,
        int x, int y, int width, int height,
        double strokeThickness, string strokeDashArray)
        : base(guid, typeId, genericTypeId, properties)
    {
        Left = x;
        Top = y;
        Width = width;
        Height = height;
        StrokeThickness = strokeThickness;
        StrokeDashArray = strokeDashArray;
    }

    internal void SetBounds(int left, int top, int width, int height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }
}
