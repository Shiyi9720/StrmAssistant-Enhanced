using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public enum ProxyModeOption
    {
        [DescriptionL("NetworkOptions_ProxyMode_Global_Global", typeof(Resources))]
        Global,

        [DescriptionL("NetworkOptions_ProxyMode_Whitelist_Whitelist", typeof(Resources))]
        Whitelist
    }

    public class NetworkOptions : EditableOptionsBase
    {
        [DisplayNameL("NetworkOptions_EditorTitle_Network", typeof(Resources))]
        public override string EditorTitle => Resources.NetworkOptions_EditorTitle_Network;

        [DisplayNameL("NetworkOptions_EnableProxyServer_Enable_Proxy_Server", typeof(Resources))]
        [DescriptionL("NetworkOptions_EnableProxyServer_Enable_Proxy_Server__Default_is_OFF_", typeof(Resources))]
        public bool EnableProxyServer { get; set; } = false;

        [DisplayNameL("NetworkOptions_ProxyServer_Proxy_Server", typeof(Resources))]
        [DescriptionL("NetworkOptions_ProxyServer_Support_http_s_proxy_server__Default_is_EMPTY_", typeof(Resources))]
        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public string ProxyServer { get; set; } = string.Empty;

        [DisplayNameL("NetworkOptions_ProxyMode_Proxy_Mode", typeof(Resources))]
        [DescriptionL("NetworkOptions_ProxyMode_Proxy_all_external_requests_or_only_whitelisted_domains__Default_is_Global_", typeof(Resources))]
        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public ProxyModeOption ProxyMode { get; set; } = ProxyModeOption.Global;

        [DisplayNameL("NetworkOptions_ProxyWhitelist_Proxy_Whitelist", typeof(Resources))]
        [DescriptionL("NetworkOptions_ProxyWhitelist_Comma_separated_domains__Subdomains_are_included__Default_is_EMPTY_", typeof(Resources))]
        [VisibleCondition(nameof(ProxyMode), ValueCondition.IsEqual, ProxyModeOption.Whitelist)]
        public string ProxyWhitelist { get; set; } = string.Empty;

        [DisplayNameL("NetworkOptions_LocalDiscoveryAddress_Local_Discovery_Address", typeof(Resources))]
        [DescriptionL("NetworkOptions_LocalDiscoveryAddress_Address_advertised_to_LAN_clients_through_UDP_discovery_and_the_public_system_info_endpoint__Set_it_to_the_local_reverse_proxy_URL__or_enter_BLOCKED_to_disable_the_UDP_reply__A_restart_is_required__Default_is_EMPTY_", typeof(Resources))]
        public string LocalDiscoveryAddress { get; set; } = string.Empty;
    }
}
