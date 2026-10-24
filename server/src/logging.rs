//! Thread-safe process logging. Each physical line carries a UTC timestamp.
use std::io::{self, Write};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

static OUTPUT: Mutex<()> = Mutex::new(());

/// Returns:
///     (i64, i64, i64): Gregorian year, month and day for UTC epoch days.
fn civil_from_days(days: i64) -> (i64, i64, i64) {
    let shifted = days + 719_468;
    let era = if shifted >= 0 {
        shifted
    } else {
        shifted - 146_096
    } / 146_097;
    let day_of_era = shifted - era * 146_097;
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let mut year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_prime = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_prime + 2) / 5 + 1;
    let month = month_prime + if month_prime < 10 { 3 } else { -9 };
    year += i64::from(month <= 2);
    (year, month, day)
}

/// Returns:
///     String: Millisecond-resolution UTC ISO timestamp without brackets.
fn timestamp(now: SystemTime) -> String {
    let elapsed = now.duration_since(UNIX_EPOCH).unwrap_or_default();
    let seconds = elapsed.as_secs();
    let (year, month, day) = civil_from_days((seconds / 86_400) as i64);
    let hour = seconds % 86_400 / 3600;
    let minute = seconds % 3600 / 60;
    let second = seconds % 60;
    format!(
        "{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{:03}Z",
        elapsed.subsec_millis()
    )
}

/// Write one complete line at a time so worker threads cannot interleave it.
pub fn emit(error: bool, message: &str) {
    let _guard = OUTPUT
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    let prefix = format!(
        "[{}] {}",
        timestamp(SystemTime::now()),
        if error { "ERROR" } else { "INFO" }
    );
    if error {
        let mut stream = io::stderr().lock();
        for line in message.lines() {
            let _ = writeln!(stream, "{prefix} {line}");
        }
        if message.is_empty() {
            let _ = writeln!(stream, "{prefix}");
        }
        let _ = stream.flush();
    } else {
        let mut stream = io::stdout().lock();
        for line in message.lines() {
            let _ = writeln!(stream, "{prefix} {line}");
        }
        if message.is_empty() {
            let _ = writeln!(stream, "{prefix}");
        }
        let _ = stream.flush();
    }
}

#[macro_export]
macro_rules! server_log {
    ($($argument:tt)*) => {
        $crate::logging::emit(false, &format!($($argument)*))
    };
}

#[macro_export]
macro_rules! server_error {
    ($($argument:tt)*) => {
        $crate::logging::emit(true, &format!($($argument)*))
    };
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    /// Keep leap dates and the epoch readable across platforms.
    fn utc_timestamp_has_readable_date_and_milliseconds() {
        assert_eq!(timestamp(UNIX_EPOCH), "1970-01-01T00:00:00.000Z");
        assert_eq!(
            timestamp(UNIX_EPOCH + std::time::Duration::from_millis(951_782_400_123)),
            "2000-02-29T00:00:00.123Z"
        );
    }
}
