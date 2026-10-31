//! Compare token-at-a-time writes with buffered durable JSON using disposable files.
//! Usage: cargo run --manifest-path server/Cargo.toml --example durable_json_bench -- /tmp/combat-copy.json
use std::fs::{self, File};
use std::io::{self, BufWriter, Write};
use std::path::Path;
use std::time::Instant;

struct CountWrites {
    file: File,
    calls: usize,
}

impl Write for CountWrites {
    /// Forward one write while counting filesystem write calls.
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.calls += 1;
        self.file.write(bytes)
    }

    /// Flush the underlying file explicitly.
    fn flush(&mut self) -> io::Result<()> {
        self.file.flush()
    }
}

/// Returns:
///     (f64, usize): Durable replacement milliseconds and underlying write calls.
/// Raises:
///     io::Error: Disposable staging or JSON serialization fails.
fn measure(
    directory: &Path,
    value: &serde_json::Value,
    buffered: bool,
) -> io::Result<(f64, usize)> {
    let published = directory.join("published.json");
    let temporary = directory.join("staged.json");
    let start = Instant::now();
    let mut output = CountWrites {
        file: File::create(&temporary)?,
        calls: 0,
    };
    if buffered {
        let mut buffer = BufWriter::with_capacity(64 * 1024, &mut output);
        serde_json::to_writer(&mut buffer, value)?;
        buffer.flush()?;
    } else {
        serde_json::to_writer(&mut output, value)?;
    }
    output.file.sync_all()?;
    let calls = output.calls;
    drop(output);
    fs::rename(temporary, &published)?;
    let milliseconds = start.elapsed().as_secs_f64() * 1_000.;
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&fs::read(published)?)?,
        *value
    );
    Ok((milliseconds, calls))
}

/// Read the supplied snapshot and compare five writes per variant without mutating the source.
/// Raises:
///     io::Error: The source snapshot or disposable benchmark files cannot be processed.
fn main() -> io::Result<()> {
    let source = std::env::args()
        .nth(1)
        .ok_or_else(|| io::Error::other("Supply a copied JSON snapshot path."))?;
    let value: serde_json::Value = serde_json::from_slice(&fs::read(source)?)?;
    let directory = std::env::temp_dir().join(format!("42-json-bench-{}", std::process::id()));
    fs::create_dir(&directory)?;
    let result = (|| {
        let mut raw = Vec::new();
        let mut buffered = Vec::new();
        for _ in 0..5 {
            raw.push(measure(&directory, &value, false)?);
            buffered.push(measure(&directory, &value, true)?);
        }
        raw.sort_by(|a, b| a.0.total_cmp(&b.0));
        buffered.sort_by(|a, b| a.0.total_cmp(&b.0));
        println!(
            "JSON bytes={}, actors={}",
            serde_json::to_vec(&value)?.len(),
            value["actors"].as_array().map_or(0, Vec::len)
        );
        println!("Median of 5 (serialize + flush + sync_all + rename): unbuffered {:.3}ms / {} writes; buffered {:.3}ms / {} writes; {:.1}x faster", raw[2].0, raw[2].1, buffered[2].0, buffered[2].1, raw[2].0 / buffered[2].0);
        beyond_server::durable_json::replace(&directory.join("production.json"), &value)?;
        assert_eq!(
            serde_json::from_slice::<serde_json::Value>(&fs::read(
                directory.join("production.json")
            )?)?,
            value
        );
        Ok(())
    })();
    fs::remove_dir_all(directory)?;
    result
}
