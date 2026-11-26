from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_log_writer_close_is_idempotent_and_rejects_writes_after_close():
    source = read("src/BotLib/LogWriter.cs")
    assert "public class LogWriter : IDisposable" in source
    assert "private readonly object _lifecycleSync = new object();" in source
    assert "private bool _isClosed;" in source
    close = source.split("public void Close(string reason)", 1)[1].split(
        "public void Dispose()", 1
    )[0]
    assert "if (_isClosed) return;" in close
    assert "_file.WriteLine(string.Format(\"日志关闭" in close
    assert "_isClosed = true;" in close
    assert "if (_file != null) _file.Close();" in close
    write = source.split("public void Write(string text", 1)[1].split(
        "public static string GetStackTrace", 1
    )[0]
    assert "if (_isClosed || _file == null) return;" in write
    assert "lock (_lifecycleSync)" in write


def test_loop_save_file_stops_its_timer_and_serializes_final_flush():
    source = read("src/BotLib/LogWriter.cs")
    loop = source.split("private class LoopSaveFile", 1)[1]
    close = loop.split("public void Close()", 1)[1].split("private void DeleteExpiredSegments", 1)[0]
    assert "private readonly object _writeSync = new object();" in loop
    assert "private int _closed;" in loop
    assert "if (Interlocked.Exchange(ref _closed, 1) != 0) return;" in close
    assert "if (timer != null) timer.Stop();" in close
    assert "lock (_writeSync) WriteLoopCore();" in close
    assert "if (_closed != 0) return;" in loop
    assert "private void WriteLoopCore()" in loop
