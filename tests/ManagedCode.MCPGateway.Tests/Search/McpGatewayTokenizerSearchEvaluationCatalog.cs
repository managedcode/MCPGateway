using System.ComponentModel;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewayTokenizerSearchEvaluationTests
{
    // The catalog intentionally reuses common operation words such as search, lookup,
    // timeline, and summary across different domains. The descriptions carry the
    // domain-specific separation so tokenizer search is evaluated on semantics, not luck.
    private static readonly EvaluationToolSpec[] EvaluationTools =
    [
        .. CreateSpecs(
            GitHubTool,
            ("github_repository_search", "Find GitHub repositories by owner, topic, star range, or programming language."),
            ("github_issue_search", "Find GitHub issues by repository, label, milestone, or bug triage keywords."),
            ("github_pull_request_search", "Search GitHub pull requests by repository, reviewer queue, review approvals, branch, or merge status. Aliases: merge request, repo review queue, demande de fusion, рев'ю пулреквестів."),
            ("github_release_notes_lookup", "Lookup GitHub release notes, tags, and changelog entries for a repository."),
            ("github_code_search", "Search GitHub source code for files, symbols, snippets, or API usages inside repositories.")),
        .. CreateSpecs(
            WeatherTool,
            ("weather_current_conditions", "Get current weather conditions, temperature, and feels-like values for a place."),
            ("weather_forecast_lookup", "Get weather forecast for a city, including rain probability, temperature trend, and wind."),
            ("weather_alert_search", "Find severe weather alerts, storm warnings, and hazard bulletins for a region. Aliases: alerta de tormenta, alerte tempete, штормове попередження."),
            ("weather_air_quality_lookup", "Lookup air quality index, smoke exposure, and pollution levels for a location."),
            ("weather_historical_climate", "Review historical climate averages, temperature normals, and past precipitation patterns.")),
        .. CreateSpecs(
            CalendarTool,
            ("calendar_find_free_slots", "Find who can meet, meeting availability, open calendar slots, and free time windows for attendees. Aliases: free slot, creneau libre, вільний слот."),
            ("calendar_create_event", "Create a calendar event with attendees, title, time window, and location."),
            ("calendar_reschedule_event", "Reschedule or move an existing meeting to a new date, time window, or attendee availability."),
            ("calendar_cancel_event", "Cancel a calendar event and notify participants or owners."),
            ("calendar_list_daily_agenda", "List daily agenda items, meetings, reminders, and upcoming schedule blocks. Aliases: agenda du jour, порядок денний.")),
        .. CreateSpecs(
            FilesystemTool,
            ("filesystem_find_files", "Find or locate files, PDFs, and documents by folder, glob pattern, extension, or text content. Not for invoice payment status or financial reconciliation."),
            ("filesystem_read_file", "Read file contents from a path, document, or note inside a workspace."),
            ("filesystem_write_file", "Write or replace file contents at a target path or note."),
            ("filesystem_move_file", "Move, rename, archive, or relocate files between folders."),
            ("filesystem_list_directory", "List directory contents, recent files, and nested folders.")),
        .. CreateSpecs(
            SupportTool,
            ("support_ticket_search", "Find support tickets by customer, issue summary, severity, or product area."),
            ("support_ticket_create", "Create a new support ticket with customer details, summary, severity, and product area."),
            ("support_ticket_update", "Update support ticket status, owner, severity, or resolution notes."),
            ("support_sla_lookup", "Lookup support SLA response times, escalation policy, and entitlement by plan."),
            ("support_customer_timeline", "Review the support timeline, escalations, and prior issues for a customer.")),
        .. CreateSpecs(
            FinanceTool,
            ("finance_exchange_rate_lookup", "Lookup currency exchange rates, FX conversions, and quote timestamps. Aliases: tipo de cambio, taux de change, валютний курс."),
            ("finance_invoice_search", "Find invoices, bills, and billing records by customer, invoice number, payment state, or due date. Aliases: facture, factura, рахунок."),
            ("finance_payment_reconciliation", "Reconcile payments, settlements, and bank references to invoices."),
            ("finance_refund_lookup", "Lookup refund requests, refund status, order reimbursement, payout status, and reversal references."),
            ("finance_tax_summary", "Summarize tax amounts, VAT, and filing totals by period or jurisdiction.")),
        .. CreateSpecs(
            TravelTool,
            ("travel_flight_search", "Search flights by origin, destination, travel date, airline, or cabin preference."),
            ("travel_hotel_search", "Find hotels by city, district, amenities, breakfast, or cancellation policy. Aliases: hotel, hôtel, готель."),
            ("travel_booking_lookup", "Lookup booking confirmation details, ticket numbers, and reservation status."),
            ("travel_itinerary_builder", "Build a travel itinerary with flights, stays, meetings, and transfer timing."),
            ("travel_ground_transport_lookup", "Find airport transfer, train, taxi, or metro options for a route.")),
        .. CreateSpecs(
            SecurityTool,
            ("security_vulnerability_search", "Find vulnerabilities, CVEs, package advisories, or image scan findings."),
            ("security_secret_rotation", "Rotate secrets, API keys, credentials, or certificates for a target system."),
            ("security_access_review", "Review user access, permissions, and privileged roles for a system or team."),
            ("security_audit_log_search", "Search audit logs for sign-ins, admin actions, and sensitive operations."),
            ("security_incident_timeline", "Review security incident timeline, responders, and evidence events.")),
        .. CreateSpecs(
            CrmTool,
            ("crm_contact_search", "Find CRM contacts and contact details by name, email, title, account, or segment. Aliases: contacto, contact, контакт."),
            ("crm_company_lookup", "Lookup CRM company records, account details, industry, or territory."),
            ("crm_deal_pipeline_search", "Search deal pipeline by account, stage, owner, amount, or forecast."),
            ("crm_activity_timeline", "Review CRM activity timeline including recent touches, calls, emails, meetings, and notes."),
            ("crm_lead_enrichment", "Enrich leads with company facts, role context, and routing signals.")),
        .. CreateSpecs(
            CommerceTool,
            ("commerce_catalog_search", "Search product catalog by keyword, category, brand, or attribute filters."),
            ("commerce_inventory_lookup", "Lookup inventory, stock by SKU, warehouse balance, and availability."),
            ("commerce_order_search", "Find customer orders by email, order number, payment status, or channel."),
            ("commerce_return_lookup", "Lookup return requests, RMA status, reasons, and refund linkage."),
            ("commerce_shipping_tracking", "Track shipment status, package tracking, parcel events, carrier scans, and delivery estimates. Aliases: suivi de colis, seguimiento de envío, відстеження посилки."))
    ];

    private static string GitHubTool(
        [Description("Repository owner or organization handle.")] string owner,
        [Description("Repository name, topic, or code area to inspect.")] string repository,
        [Description("Search words, labels, reviewers, tags, or symbol names.")] string query,
        [Description("Desired work item state such as open, closed, or merged.")] WorkItemState state)
        => $"{owner}:{repository}:{query}:{state}";

    private static string WeatherTool(
        [Description("City, region, airport code, or destination name.")] string location,
        [Description("Time range such as now, today, weekend, or next 5 days.")] string timeRange,
        [Description("Weather focus such as rain, wind, storms, smoke, or pollution.")] string focus,
        [Description("Preferred temperature unit.")] TemperatureUnit unit)
        => $"{location}:{timeRange}:{focus}:{unit}";

    private static string CalendarTool(
        [Description("Person, attendee, or room involved in the meeting request.")] string attendee,
        [Description("Date or day phrase such as today, tomorrow, or next friday.")] string date,
        [Description("Time window such as morning, afternoon, or 14:00-16:00.")] string timeWindow,
        [Description("Meeting title, agenda, or subject to create or change.")] string subject)
        => $"{attendee}:{date}:{timeWindow}:{subject}";

    private static string FilesystemTool(
        [Description("Root folder, workspace path, or directory to inspect.")] string rootPath,
        [Description("File name, glob pattern, extension, or exact target.")] string filePattern,
        [Description("Text to read, write, match, or move alongside the file operation.")] string contentOrDestination,
        [Description("Filesystem intent such as find, read, write, move, or list.")] FileIntent intent)
        => $"{rootPath}:{filePattern}:{contentOrDestination}:{intent}";

    private static string SupportTool(
        [Description("Customer account, company, or requester name.")] string customer,
        [Description("Issue summary, symptom, or ticket subject to investigate.")] string issueQuery,
        [Description("Requested severity or urgency for the support workflow.")] TicketSeverity severity,
        [Description("Product area, service, or plan linked to the support request.")] string productArea)
        => $"{customer}:{issueQuery}:{severity}:{productArea}";

    private static string FinanceTool(
        [Description("Customer, vendor, or ledger subject linked to the finance request.")] string party,
        [Description("Invoice number, refund reference, currency pair, or tax period.")] string reference,
        [Description("Money amount, due state, or reconciliation clue such as unpaid or settled.")] string amountOrState,
        [Description("Finance operation such as invoice, refund, exchange, reconciliation, or tax.")] FinanceIntent intent)
        => $"{party}:{reference}:{amountOrState}:{intent}";

    private static string TravelTool(
        [Description("Departure city, airport, or transfer starting point.")] string origin,
        [Description("Destination city, hotel district, venue, or route target.")] string destination,
        [Description("Travel date, stay window, or itinerary timing.")] string travelDate,
        [Description("Preference such as nonstop, breakfast, cancellation, or rail transfer.")] string preference)
        => $"{origin}:{destination}:{travelDate}:{preference}";

    private static string SecurityTool(
        [Description("Host, image, system, account, or secret target to inspect.")] string asset,
        [Description("Timeframe, event window, or audit period to search.")] string timeWindow,
        [Description("Severity level, sensitivity, or privilege scope.")] string severityOrScope,
        [Description("Security operation such as vulnerability, rotation, access, audit, or incident.")] string securityIntent)
        => $"{asset}:{timeWindow}:{severityOrScope}:{securityIntent}";

    private static string CrmTool(
        [Description("Contact, company, account, or lead to search or enrich.")] string entity,
        [Description("Email, stage, territory, or identifying CRM qualifier.")] string qualifier,
        [Description("Time range for activity history, pipeline review, or follow-up window.")] string timeWindow,
        [Description("CRM workflow such as contact lookup, activity review, deal search, or enrichment.")] string crmIntent)
        => $"{entity}:{qualifier}:{timeWindow}:{crmIntent}";

    private static string CommerceTool(
        [Description("SKU, order number, tracking id, or return reference.")] string orderOrSku,
        [Description("Customer email, buyer name, or account identifier.")] string customer,
        [Description("Sales channel, warehouse, or current status like shipped or returned.")] string statusOrLocation,
        [Description("Commerce workflow such as catalog, inventory, order, return, or tracking.")] string commerceIntent)
        => $"{orderOrSku}:{customer}:{statusOrLocation}:{commerceIntent}";
}
