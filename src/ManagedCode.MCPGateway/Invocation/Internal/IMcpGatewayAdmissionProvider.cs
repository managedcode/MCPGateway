namespace ManagedCode.MCPGateway;

internal interface IMcpGatewayAdmissionProvider
{
    IMcpGatewaySourceAdmission Admission { get; }
}
