<%@ Page Title="Rate search" Language="C#" MasterPageFile="~/Site.Master" AutoEventWireup="true" CodeBehind="Default.aspx.cs" Inherits="TariffHub._Default" %>

<asp:Content ID="Main" ContentPlaceHolderID="MainContent" runat="server">
    <%-- Partial page updates: changing mode, carrier or a country, and Search, refresh only the panel below
         in the background - no full reload, no white flash. Scripts come from the app itself (no CDN). --%>
    <asp:ScriptManager ID="sm" runat="server" EnablePartialRendering="true" EnableCdn="false" />
    <asp:UpdateProgress ID="upBusy" runat="server" AssociatedUpdatePanelID="upMain" DisplayAfter="200">
        <ProgressTemplate><div class="busy">Loading…</div></ProgressTemplate>
    </asp:UpdateProgress>
    <asp:UpdatePanel ID="upMain" runat="server" UpdateMode="Always" RenderMode="Block">
    <ContentTemplate>
    <asp:Literal ID="litMessages" runat="server" />

    <div class="grid">
        <fieldset>
            <legend>Shipment</legend>
            <label>Ship date <asp:TextBox ID="txtShipDate" runat="server" TextMode="Date" /></label>
            <label title="Which tariff table is searched. Ground and Air are separate tables with different columns, so changing this reloads every filter below.">Mode (transport)
                <asp:DropDownList ID="ddlMode" runat="server" AutoPostBack="true">
                    <asp:ListItem Value="Ground" Text="Ground" />
                    <asp:ListItem Value="Air" Text="Air" />
                </asp:DropDownList>
            </label>
            <label>Carrier <asp:DropDownList ID="ddlCarrier" runat="server" AutoPostBack="true" /></label>
            <%= Filter("service", "All services", "The carrier's service: LTL, full truck, container, oversize, dedicated truck, surface.") %>
            <%= Filter("level", "All levels", "Air service level (SL0 fastest to SL3 slowest), as priced by the carrier.") %>
            <%= Filter("piece", "Any piece type", "Parcel carriers price a letter, a document, a pak, a package and a hundredweight consignment differently off the same lane.") %>
            <%= Filter("delivery", "Any address type", "Some carriers charge more to a residential address than a commercial one on the identical lane.") %>
            <%= Filter("lanetype", "Any", "Route type as written in the tariff file.") %>
            <%= Filter("ratetag", "Contract and spot", null) %>
        </fieldset>

        <fieldset>
            <legend>From</legend>
            <label>Country <asp:DropDownList ID="ddlOriginCountry" runat="server" AutoPostBack="true" /></label>
            <% if (ShowAirports) { %>
                <label title="Airports of the chosen country that have air lanes. Country-wide lanes are included too.">Airport <%= AirportSelect("origin") %></label>
            <% } %>
            <asp:PlaceHolder ID="phOriginPlace" runat="server">
                <label title="Lists the places in the chosen country.">Place <%= PlaceSelect("origin") %></label>
                <asp:PlaceHolder ID="phOriginPostcode" runat="server">
                    <label>Postcode <asp:TextBox ID="txtOriginPostcode" runat="server" placeholder="optional" /></label>
                </asp:PlaceHolder>
                <p class="small muted">Lanes priced for the whole country, by carrier zone or by a district that
                    contains the place are included too.</p>
            </asp:PlaceHolder>
            <asp:PlaceHolder ID="phOriginNoPlace" runat="server">
                <p class="small muted">Lanes in this tariff are stated country to country, so there is no place to pick.</p>
            </asp:PlaceHolder>
        </fieldset>

        <fieldset>
            <legend>To</legend>
            <label>Country <asp:DropDownList ID="ddlDestCountry" runat="server" AutoPostBack="true" /></label>
            <% if (ShowAirports) { %>
                <label title="Airports of the chosen country that have air lanes. Country-wide lanes are included too.">Airport <%= AirportSelect("dest") %></label>
            <% } %>
            <asp:PlaceHolder ID="phDestPlace" runat="server">
                <label title="Lists the places in the chosen country.">Place <%= PlaceSelect("dest") %></label>
                <asp:PlaceHolder ID="phDestPostcode" runat="server">
                    <label>Postcode <asp:TextBox ID="txtDestPostcode" runat="server" placeholder="optional" /></label>
                </asp:PlaceHolder>
            </asp:PlaceHolder>
            <asp:PlaceHolder ID="phDestNoPlace" runat="server">
                <p class="small muted">Lanes in this tariff are stated country to country, so there is no place to pick.</p>
            </asp:PlaceHolder>
            <p class="small muted">Leave empty for a dedicated pick-up or drop truck, which has no destination.</p>
        </fieldset>

        <fieldset>
            <legend>Cargo</legend>
            <%= Filter("cargo", "Any", null) %>
            <%= Filter("temperature", "Any", null) %>
            <label>Weight (kg) <asp:TextBox ID="txtWeight" runat="server" TextMode="Number" step="any" min="0" /></label>
            <label>Volume (cbm) <asp:TextBox ID="txtVolume" runat="server" TextMode="Number" step="any" min="0" /></label>
            <div class="dims">
                <span class="muted">or L × W × H cm × pieces</span>
                <asp:TextBox ID="txtLength" runat="server" TextMode="Number" step="any" min="0" placeholder="L" aria-label="Length cm" />
                <asp:TextBox ID="txtWidth" runat="server" TextMode="Number" step="any" min="0" placeholder="W" aria-label="Width cm" />
                <asp:TextBox ID="txtHeight" runat="server" TextMode="Number" step="any" min="0" placeholder="H" aria-label="Height cm" />
                <asp:TextBox ID="txtPieces" runat="server" TextMode="Number" min="1" placeholder="pcs" aria-label="Pieces" />
            </div>
        </fieldset>

        <asp:PlaceHolder ID="phVehicle" runat="server">
            <fieldset>
                <legend>Vehicle and distance</legend>
                <%= Filter("equipment", "All sizes", null) %>
                <%= Filter("oversize", "All classes", null) %>
                <label>Distance (km, dedicated truck) <asp:TextBox ID="txtDistance" runat="server" TextMode="Number" step="any" min="0" /></label>
                <asp:Literal ID="litDistanceNote" runat="server" />
            </fieldset>
        </asp:PlaceHolder>

        <div class="actions">
            <asp:Button ID="btnSearch" runat="server" Text="Search" CssClass="primary" />
            <a class="button" href="<%: ResolveUrl("~/") %>">Clear</a>
        </div>
    </div>

    <asp:Repeater ID="rptGaps" runat="server" ItemType="TariffHub.Models.AdderGapRow">
        <ItemTemplate>
            <div class="info">
                <b><%#: Item.CarrierCode %></b> <%#: Item.ServiceType %> lane <%#: Item.LaneCode %>:
                no band covers this weight (<%#: Num(Item.ChargeableKg) %> kg chargeable); this carrier charges an adder above <%#: Num(Item.TopBandKg) %> kg.
            </div>
        </ItemTemplate>
    </asp:Repeater>

    <asp:Literal ID="litResultHeading" runat="server" />

    <asp:PlaceHolder ID="phResults" runat="server" Visible="false">
        <div class="scroll results">
            <table>
                <thead>
                    <tr>
                        <th>Carrier</th><th>Service</th><th>Lane</th><th>Type</th><th>From</th><th>To</th>
                        <th>Band</th><th>Transit</th><th>Rate</th><th>Chg. kg</th><th>Freight</th>
                        <th>Surcharges / fuel</th><th title="Optional charges of this service. Open to see each one priced for this shipment and tick the ones that apply.">Accessorials</th>
                        <th>Estimated total</th>
                    </tr>
                </thead>
                <tbody>
                    <asp:Repeater ID="rptResults" runat="server" ItemType="TariffHub.Models.SearchResultRow">
                        <ItemTemplate>
                            <tr<%# RowClass(Item) %>>
                                <td><%#: Item.CarrierCode %></td>
                                <td class="nw" title="<%#: ServiceTitle(Item) %>"><%# ServiceCell(Item) %></td>
                                <td class="nw"><span class="lane" title="<%#: LaneTitle(Item) %>"><%#: Item.LaneCode %></span><%# Item.ZoneDependsOnAddress ? "<span class=\"tag warn\" title=\"This carrier splits the country by service area: the zone, and so the price, depends on the exact address. A postcode-to-service-area list from the carrier is needed to pick one.\">depends on address</span>" : "" %></td>
                                <td><%#: CodeLabel("lane_type", Item.LaneType) %></td>
                                <td class="nw" title="<%#: Item.OriginTitle %>"><%#: Item.OriginDesc %></td>
                                <td class="nw" title="<%#: Item.DestTitle %>"><%#: Item.DestDesc ?? "—" %></td>
                                <td class="nw"><%#: Band(Item.WeightFromKg, Item.WeightToKg, "kg") %> <%#: Band(Item.DistanceFromKm, Item.DistanceToKm, "km") %></td>
                                <td class="nw"><%#: Item.TransitTime ?? "" %></td>
                                <td class="num"><%#: Rate(Item.Rate) %>
                                    <span class="small muted"><%#: BasisLabel(Item.ChargeBasis) %></span></td>
                                <td class="num"><%#: Num(Item.ChargeableKg) %></td>
                                <td class="num"><%# FreightCell(Item) %></td>
                                <td class="num"><%# ChargesCell(Item) %></td>
                                <td class="num"><%# AccessorialCell(Item) %></td>
                                <td class="num total"><%# TotalCell(Item) %></td>
                            </tr>
                            <%# AccessorialDetailRow(Item) %>
                        </ItemTemplate>
                    </asp:Repeater>
                </tbody>
            </table>
        </div>
        <p class="small muted">Rows are ordered by currency, then service, cheapest first; a heavier line marks where the service changes.
            Totals are never compared across currencies. Accessorials are not in the total until you tick them. Hover a lane code for its validity and source.</p>
    </asp:PlaceHolder>
    </ContentTemplate>
    </asp:UpdatePanel>

    <script>
        // Open / close a row's accessorial breakdown (the row right after it).
        function accToggle(btn) {
            var detail = btn.closest('tr').nextElementSibling;
            if (!detail || !detail.classList.contains('acc-row')) return;
            var open = detail.hidden;
            detail.hidden = !open;
            btn.setAttribute('aria-expanded', open ? 'true' : 'false');
            btn.querySelector('.arr').textContent = open ? '\u25B4' : '\u25BE';
        }
        function accMoney(v) {
            return v.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
        }
        // Re-add the ticked accessorials to the row's total. Amounts are in cents to avoid float drift.
        function accRecalc(cb) {
            var detail = cb.closest('tr.acc-row'), row = detail.previousElementSibling;
            var cents = 0, n = 0;
            detail.querySelectorAll('input.acc-pick:checked').forEach(function (x) { cents += Math.round(parseFloat(x.dataset.amt) * 100); n++; });
            var sum = cents / 100, tot = row.querySelector('.tot'), sel = row.querySelector('.acc-sel');
            detail.querySelector('.acc-sum').textContent = accMoney(sum);
            if (sel) sel.textContent = n === 0 ? '' : '+' + accMoney(sum) + ' added';
            if (tot && tot.dataset.base) {
                var grand = (Math.round(parseFloat(tot.dataset.base) * 100) + cents) / 100;
                tot.textContent = accMoney(grand) + ' ' + tot.dataset.ccy;
                detail.querySelector('.acc-grand').textContent = accMoney(grand);
                row.classList.toggle('with-acc', n > 0);
            }
        }
    </script>
</asp:Content>
