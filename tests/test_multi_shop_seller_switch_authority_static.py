from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_cdp_seller_switched_event_requires_exact_authoritative_shop_session():
    client = read("src/Bot/ChromeNs/CDPClient.cs")
    method = client[
        client.index("private void SellerSwitched(string response)"):
        client.index("public async Task<T> InvokeMTop<T>")
    ]
    assert "IsInvalidated || IsShopIdentityRetired" in method
    assert "ResolveRuntimeBySellerIdentity(localUser.LoginID)" in method
    assert "string.Equals(boundShopKey, shop.ShopKey, StringComparison.Ordinal)" in method
    assert "server.IsAuthoritativeShopSession(shop.ShopKey, SessionId)" in method
    assert method.index("IsAuthoritativeShopSession") < method.index("EvSellerSwitched(this")
    assert "IsShopIdentityRetired = true" in method


def test_qn_does_not_change_seller_buyer_or_curqn_from_stale_seller_switch_event():
    qn = read("src/Bot/ChromeNs/QN.cs")
    method = qn[
        qn.index("private void Cdp_EvSellerSwitched"):
        qn.index("private Task ProcessIncomingMessageAsync")
    ]
    assert "!ReferenceEquals(CDP, client)" in method
    assert "client.IsInvalidated || client.IsShopIdentityRetired" in method
    assert "ShopContextLocator.ResolveRuntimeBySellerIdentity(e.Seller)" in method
    assert "!string.Equals(client.ShopKey, shop.ShopKey, StringComparison.Ordinal)" in method
    assert "server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)" in method
    guard = method.index("server.IsAuthoritativeShopSession")
    assert guard < method.index("Seller = e.Seller") < method.index("CurQN = this")
    assert guard < method.index("Buyer = e.Buyer")
