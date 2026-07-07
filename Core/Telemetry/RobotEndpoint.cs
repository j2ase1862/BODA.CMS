namespace BODA.CMS.Core.Telemetry
{
    /// <summary>
    /// 텔레메트리 소스가 접속할 대상. 포트를 지정하지 않으면(null) 드라이버가
    /// 자기 채널의 기본 포트(<see cref="RobotCapabilities.DefaultPort"/>)를 쓴다.
    /// </summary>
    public sealed record RobotEndpoint(string Host, int? Port = null);
}
