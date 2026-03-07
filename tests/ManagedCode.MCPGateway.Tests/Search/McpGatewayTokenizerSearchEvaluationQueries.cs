namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewayTokenizerSearchEvaluationTests
{
    private static readonly EvaluationQuerySpec[] HighRelevanceQueries =
    [
        new("developer review queue for managedcode repo", "github_pull_request_search"),
        new("latest changelog tag for repository", "github_release_notes_lookup"),
        new("where is AddManagedCodeMcpGateway defined", "github_code_search"),
        new("rain this weekend in paris", "weather_forecast_lookup"),
        new("smoke and pollution level in warsaw", "weather_air_quality_lookup"),
        new("storm warning near berlin tonight", "weather_alert_search"),
        new("who can meet tomorrow after lunch", "calendar_find_free_slots"),
        new("move design sync to friday morning", "calendar_reschedule_event"),
        new("show my agenda for today", "calendar_list_daily_agenda"),
        new("where did the invoice pdf go in reports", "filesystem_find_files"),
        new("open docs readme file", "filesystem_read_file"),
        new("rename archive.zip into backups folder", "filesystem_move_file"),
        new("enterprise customer tickets about login timeout", "support_ticket_search"),
        new("what sla does platinum support get", "support_sla_lookup"),
        new("unpaid invoice for acme", "finance_invoice_search"),
        new("eur usd exchange rate today", "finance_exchange_rate_lookup"),
        new("cheap nonstop flight paris berlin", "travel_flight_search"),
        new("hotel near conference center with breakfast", "travel_hotel_search"),
        new("critical cves in nginx image", "security_vulnerability_search"),
        new("who accessed admin panel yesterday", "security_audit_log_search"),
        new("find anna@example.com in contacts", "crm_contact_search"),
        new("track shipment 1z999", "commerce_shipping_tracking"),
        new("is sku keyboard-ergo available in warehouse", "commerce_inventory_lookup")
    ];

    private static readonly EvaluationQuerySpec[] BorderlineQueries =
    [
        new("status of refund for order 1488", "finance_refund_lookup", "commerce_return_lookup"),
        new("recent touches for contoso account", "crm_activity_timeline", "support_customer_timeline"),
        new("customer timeline with escalations for contoso", "support_customer_timeline", "crm_activity_timeline"),
        new("meeting notes and customer calls for contoso", "crm_activity_timeline", "calendar_list_daily_agenda"),
        new("order payment issue with refund history", "finance_refund_lookup", "commerce_return_lookup", "support_ticket_search")
    ];

    private static readonly EvaluationQuerySpec[] MultilingualQueries =
    [
        new("demande de fusion pour le depot managedcode", "github_pull_request_search"),
        new("alerta de tormenta en berlin esta noche", "weather_alert_search"),
        new("хто має вільний слот завтра після обіду", "calendar_find_free_slots"),
        new("рахунок для клієнта acme", "finance_invoice_search"),
        new("trouver un hôtel avec petit déjeuner près du centre", "travel_hotel_search"),
        new("suivi de colis 1z999", "commerce_shipping_tracking"),
        new("buscar contacto anna@example.com", "crm_contact_search"),
        new("порядок денний на сьогодні", "calendar_list_daily_agenda")
    ];

    private static readonly EvaluationQuerySpec[] TypoQueries =
    [
        new("review qeue for managedcode prs", "github_pull_request_search"),
        new("weather forcast rain in paris weekend", "weather_forecast_lookup"),
        new("whos free tomorrow afternon", "calendar_find_free_slots"),
        new("open the readme fie in docs", "filesystem_read_file"),
        new("unpaid invoce for acme", "finance_invoice_search"),
        new("track shipmnt 1z999", "commerce_shipping_tracking"),
        new("critical cve in nginx imgae", "security_vulnerability_search"),
        new("contcat anna@example.com", "crm_contact_search")
    ];

    private static readonly EvaluationQuerySpec[] WeakIntentQueries =
    [
        new("managedcode review approvals", "github_pull_request_search"),
        new("air bad in warsaw", "weather_air_quality_lookup"),
        new("who is free after lunch tomorrow", "calendar_find_free_slots"),
        new("invoice pdf in reports", "filesystem_find_files", "finance_invoice_search"),
        new("money back for order 1488", "finance_refund_lookup", "commerce_return_lookup"),
        new("where is the parcel", "commerce_shipping_tracking"),
        new("admin actions yesterday", "security_audit_log_search"),
        new("anna contact details", "crm_contact_search"),
        new("hotel breakfast near venue berlin", "travel_hotel_search")
    ];

    private static readonly string[] IrrelevantQueries =
    [
        "best sourdough hydration ratio for rye bread",
        "how to tune a jazz drum solo for swing practice",
        "dragon habitat migration pattern in fantasy novels",
        "origami crane folding sequence for beginners",
        "volcanic lava viscosity classroom experiment",
        "ambient techno playlist for yoga sunset session",
        "ancient pottery glaze chemistry reference table",
        "ballet toe shoe ribbon sewing tutorial"
    ];
}
