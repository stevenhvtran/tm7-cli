using System.Runtime.Serialization;

namespace Tm7.Cli.Model;

[DataContract(Name = "Line", Namespace = "http://schemas.datacontract.org/2004/07/ThreatModeling.Model.Abstracts")]
public abstract class SerializableLine : SerializableTaggable
{
    [DataMember(Name = "HandleX")]
    public int MPX { get; private set; }

    [DataMember(Name = "HandleY")]
    public int MPY { get; private set; }

    [DataMember(Name = "PortSource")]
    public StencilConnectionPort PortSource { get; private set; }

    [DataMember(Name = "PortTarget")]
    public StencilConnectionPort PortTarget { get; private set; }

    [DataMember(Name = "SourceGuid")]
    public Guid SourceGuid { get; private set; }

    [DataMember(Name = "SourceX")]
    public int X0 { get; private set; }

    [DataMember(Name = "SourceY")]
    public int Y0 { get; private set; }

    [DataMember(Name = "StrokeDashArray")]
    public string StrokeDashArray { get; private set; }

    [DataMember(Name = "StrokeThickness")]
    public double StrokeThickness { get; private set; }

    [DataMember(Name = "TargetGuid")]
    public Guid TargetGuid { get; private set; }

    [DataMember(Name = "TargetX")]
    public int X1 { get; private set; }

    [DataMember(Name = "TargetY")]
    public int Y1 { get; private set; }

    protected SerializableLine(Guid guid, string typeId, string genericTypeId,
        IEnumerable<SerializableDisplayAttribute> properties,
        Guid targetGuid, Guid sourceGuid,
        StencilConnectionPort portTarget, StencilConnectionPort portSource,
        int sourceX, int sourceY, int targetX, int targetY,
        int handleX, int handleY,
        double strokeThickness, string strokeDashArray)
        : base(guid, typeId, genericTypeId, properties)
    {
        TargetGuid = targetGuid;
        SourceGuid = sourceGuid;
        PortTarget = portTarget;
        PortSource = portSource;
        X0 = sourceX;
        Y0 = sourceY;
        X1 = targetX;
        Y1 = targetY;
        MPX = handleX;
        MPY = handleY;
        StrokeThickness = strokeThickness;
        StrokeDashArray = strokeDashArray;
    }

    internal void SetGeometry(
        int sourceX,
        int sourceY,
        int targetX,
        int targetY,
        int handleX,
        int handleY,
        StencilConnectionPort portSource,
        StencilConnectionPort portTarget)
    {
        X0 = sourceX;
        Y0 = sourceY;
        X1 = targetX;
        Y1 = targetY;
        MPX = handleX;
        MPY = handleY;
        PortSource = portSource;
        PortTarget = portTarget;
    }
}
