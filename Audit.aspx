<%@ Page Title="Search log" Language="C#" MasterPageFile="~/Site.Master" AutoEventWireup="true" CodeBehind="Audit.aspx.cs" Inherits="TariffHub.Audit" %>

<asp:Content ID="Main" ContentPlaceHolderID="MainContent" runat="server">
    <p class="muted">Every search run from the site: who ran it (caller IP), from where, what filters they applied and how many rates came back. Read live.</p>
    <asp:Literal ID="litAudit" runat="server" />
</asp:Content>
