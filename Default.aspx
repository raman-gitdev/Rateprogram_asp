<%@ Page Title="Ground Tariff Hub" Language="C#" MasterPageFile="~/Site.Master" AutoEventWireup="true" CodeBehind="Default.aspx.cs" Inherits="TariffHub._Default" %>

<asp:Content ID="Main" ContentPlaceHolderID="MainContent" runat="server">
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
            <%= Filter("piece", "Any piece type", "Parcel carriers price a letter, a document, a pak, a package and a hundredweight consignment differently off the same lane.") %>
            <%= Filter("delivery", "Any address type", "Some carriers charge more to a residential address than a commercial one on the identical lane.") %>
            <%= Filter("lanetype", "Any", "Route type as written in the tariff file.") %>
            <%= Filter("ratetag", "Contract and spot", null) %>
        </fieldset>

        <fieldset>
            <legend>From</legend>
            <label>Country <asp:DropDownList ID="ddlOriginCountry" runat="server" AutoPostBack="true" /></label>
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
                no band covers this weight (<%#: Num(Item.ChargeableKg) %> kg chargeable); this carrier charges an adder above <%#: Num(Item.TopBandKg) %> kg —
                see the <a class="link" href="<%#: LaneUrl(Item.CarrierCode, Item.LaneCode) %>">lane detail</a>.
            </div>
        </ItemTemplate>
    </asp:Repeater>

    <asp:Literal ID="litResultHeading" runat="server" />

    <asp:PlaceHolder ID="phResults" runat="server" Visible="false">
        <div class="scroll">
            <table>
                <thead>
                    <tr>
                        <th>Carrier</th><th>Service</th><th>Lane</th><th>Type</th><th>From</th><th>To</th>
                        <th>Band</th><th>Rate</th><th>Chg. kg</th><th>Min</th><th>Base freight</th>
                        <th>Auto surcharges</th><th>Fuel</th><th>Estimated total</th><th>Valid</th><th>Source</th>
                    </tr>
                </thead>
                <tbody>
                    <asp:Repeater ID="rptResults" runat="server" ItemType="TariffHub.Models.SearchResultRow">
                        <ItemTemplate>
                            <tr<%# MinApplied(Item) ? " class=\"min\"" : "" %>>
                                <td><%#: Item.CarrierCode %></td>
                                <td title="<%#: Item.ServiceType %>"><%#: CodeLabel("service_type", Item.ServiceType) %></td>
                                <td><a class="link" href="<%#: LaneUrl(Item.CarrierCode, Item.LaneCode) %>" title="Open the whole lane"><%#: Item.LaneCode %></a><%# Item.ZoneDependsOnAddress ? "<span class=\"tag warn\" title=\"This carrier splits the country by service area: the zone, and so the price, depends on the exact address. A postcode-to-service-area list from the carrier is needed to pick one.\">depends on address</span>" : "" %></td>
                                <td><%#: CodeLabel("lane_type", Item.LaneType) %></td>
                                <td><%#: Item.OriginDesc %></td>
                                <td><%#: Item.DestDesc ?? "—" %></td>
                                <td><%#: Band(Item.WeightFromKg, Item.WeightToKg, "kg") %> <%#: Band(Item.DistanceFromKm, Item.DistanceToKm, "km") %></td>
                                <td class="num"><%#: Rate(Item.Rate) %>
                                    <span class="small muted"><%#: BasisLabel(Item.ChargeBasis) %></span></td>
                                <td class="num"><%#: Num(Item.ChargeableKg) %></td>
                                <td class="num"><%#: Item.MinCharge == null ? "" : Money(Item.MinCharge) %>
                                    <%# MinApplied(Item) ? "<span class=\"tag\">applied</span>" : "" %></td>
                                <td class="num"><%#: Money(Item.BaseFreight) %></td>
                                <td class="num"><%#: Money(Item.SurchargeAmount) %>
                                    <div class="small muted"><%#: Item.SurchargeCodes %></div></td>
                                <td class="num"><%# FuelCell(Item) %></td>
                                <td class="num total"><%# TotalCell(Item) %></td>
                                <td><%#: Validity(Item) %></td>
                                <td class="small"><%#: Item.SourceRef %> · <%#: Item.SourceSheet %><%#: Item.SourceRow == null ? "" : " row " + Item.SourceRow %></td>
                            </tr>
                        </ItemTemplate>
                    </asp:Repeater>
                </tbody>
            </table>
        </div>
        <p class="small muted">Rows are grouped by currency and ordered cheapest first within each currency; totals are never compared across currencies.</p>
    </asp:PlaceHolder>
</asp:Content>
