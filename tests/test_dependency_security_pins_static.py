from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_legacy_winforms_dependencies_are_pinned_to_patched_compatible_versions():
    bot_packages = read("src/Bot/packages.config")
    botlib_packages = read("src/BotLib/packages.config")
    bot_project = read("src/Bot/Bot.csproj")
    botlib_project = read("src/BotLib/BotLib.csproj")
    app_config = read("src/Bot/App.config")

    assert '<package id="log4net" version="3.6.0"' in bot_packages
    assert "log4net.3.6.0\\lib\\net462\\log4net.dll" in bot_project
    assert 'Version=3.6.0.0' in bot_project
    assert 'newVersion="3.6.0.0"' in app_config

    assert '<package id="Newtonsoft.Json" version="13.0.3"' in botlib_packages
    assert "Newtonsoft.Json.13.0.3\\lib\\net45\\Newtonsoft.Json.dll" in botlib_project
    assert 'Version=13.0.0.0' in botlib_project

    assert '<package id="System.Security.Cryptography.Pkcs" version="6.0.3"' in bot_packages
    assert "System.Security.Cryptography.Pkcs.6.0.3\\lib\\net461\\System.Security.Cryptography.Pkcs.dll" in bot_project
