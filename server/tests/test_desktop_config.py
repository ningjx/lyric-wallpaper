from server.config import ServerConfig, load_config, save_config


def test_desktop_config_round_trip(tmp_path):
    cfg = ServerConfig()
    cfg.music.enable_netease = False
    cfg.music.enable_apple = True
    cfg.cache.data_dir = "C:/Users/Test/AppData/Local/LyricServer"
    path = tmp_path / "config.json"

    save_config(cfg, str(path))
    loaded = load_config(str(path))

    assert loaded.music.enable_netease is False
    assert loaded.music.enable_apple is True
    assert loaded.cache.data_dir == cfg.cache.data_dir
