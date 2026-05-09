using System;
using System.Globalization;
using System.IO;
using Npgsql;

// ---------------------------------------------------------------------------
// Configuration – update these before running
// ---------------------------------------------------------------------------
const string ConnectionString =
    "Host=localhost;Port=5432;Database=imdb;Username=postgres;Password=postgres";

// Resolve the project root via the compiler-injected source file path
string projectRoot = GetSourceFileDirectory();
var csvFiles = Directory.GetFiles(projectRoot, "*.csv", SearchOption.TopDirectoryOnly);

if (csvFiles.Length == 0)
{
    Console.Error.WriteLine("ERROR: No .csv file found in the project directory.");
    return;
}

string CsvFilePath = csvFiles[0];
Console.WriteLine($"Found CSV: {CsvFilePath}");
// ---------------------------------------------------------------------------

await using var conn = new NpgsqlConnection(ConnectionString);
await conn.OpenAsync();

// Watchlist CSV columns (0-indexed):
//  0  Position
//  1  Const
//  2  Created
//  3  Modified
//  4  Description
//  5  Title
//  6  Original Title
//  7  URL
//  8  Title Type
//  9  IMDb Rating
//  10 Runtime (mins)
//  11 Year
//  12 Genres
//  13 Num Votes
//  14 Release Date
//  15 Directors
//  16 Your Rating
//  17 Date Rated

// Create the table if it doesn't already exist
await using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS imdb_watchlist (
            const           VARCHAR(20)     PRIMARY KEY,
            position        INTEGER,
            created         DATE,
            modified        DATE,
            description     TEXT,
            title           TEXT,
            original_title  TEXT,
            url             TEXT,
            title_type      VARCHAR(50),
            imdb_rating     NUMERIC(3,1),
            runtime_mins    SMALLINT,
            year            SMALLINT,
            genres          TEXT,
            num_votes       INTEGER,
            release_date    TEXT,
            directors       TEXT,
            your_rating     SMALLINT,
            date_rated      DATE
        );
        """;
    await cmd.ExecuteNonQueryAsync();
}

Console.WriteLine("Table ready. Reading CSV…");

int inserted = 0, skipped = 0, errors = 0;

using var reader = new StreamReader(CsvFilePath);

// Skip header line
string? header = await reader.ReadLineAsync();
if (header is null)
{
    Console.WriteLine("CSV file is empty.");
    return;
}

string? line;
int lineNumber = 1;
while ((line = await reader.ReadLineAsync()) is not null)
{
    lineNumber++;
    if (string.IsNullOrWhiteSpace(line)) continue;

    try
    {
        // Parse CSV respecting quoted fields
        string[] fields = SplitCsvLine(line);

        if (fields.Length < 18)
        {
            Console.WriteLine($"Line {lineNumber}: too few fields ({fields.Length}), skipping.");
            skipped++;
            continue;
        }

        int?     position     = ParseInt(fields[0]);
        string   @const       = fields[1].Trim();
        DateOnly? created     = ParseDate(fields[2]);
        DateOnly? modified    = ParseDate(fields[3]);
        string   description  = fields[4].Trim();
        string   title        = fields[5].Trim();
        string   originalTitle = fields[6].Trim();
        string   url          = fields[7].Trim();
        string   titleType    = fields[8].Trim();
        decimal? imdbRating   = ParseDecimal(fields[9]);
        short?   runtimeMins  = ParseShort(fields[10]);
        short?   year         = ParseShort(fields[11]);
        string   genres       = fields[12].Trim();
        int?     numVotes     = ParseInt(fields[13]);
        string   releaseDate  = fields[14].Trim();
        string   directors    = fields[15].Trim();
        short?   yourRating   = ParseShort(fields[16]);
        DateOnly? dateRated   = ParseDate(fields[17]);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO imdb_watchlist
                (const, position, created, modified, description, title, original_title,
                 url, title_type, imdb_rating, runtime_mins, year,
                 genres, num_votes, release_date, directors, your_rating, date_rated)
            VALUES
                (@const, @position, @created, @modified, @description, @title, @originalTitle,
                 @url, @titleType, @imdbRating, @runtimeMins, @year,
                 @genres, @numVotes, @releaseDate, @directors, @yourRating, @dateRated)
            ON CONFLICT (const) DO NOTHING;
            """;

        AddParam(cmd, "const", @const);
        AddNullableParam(cmd, "position", position);
        AddNullableParam(cmd, "created", created);
        AddNullableParam(cmd, "modified", modified);
        AddParam(cmd, "description", description);
        AddParam(cmd, "title", title);
        AddParam(cmd, "originalTitle", originalTitle);
        AddParam(cmd, "url", url);
        AddParam(cmd, "titleType", titleType);
        AddNullableParam(cmd, "imdbRating", imdbRating);
        AddNullableParam(cmd, "runtimeMins", runtimeMins);
        AddNullableParam(cmd, "year", year);
        AddParam(cmd, "genres", genres);
        AddNullableParam(cmd, "numVotes", numVotes);
        AddParam(cmd, "releaseDate", releaseDate);
        AddParam(cmd, "directors", directors);
        AddNullableParam(cmd, "yourRating", yourRating);
        AddNullableParam(cmd, "dateRated", dateRated);

        int rowsAffected = await cmd.ExecuteNonQueryAsync();
        if (rowsAffected == 0)
        {
            Console.WriteLine($"  Row '{@const}' already exists (skipping duplicate).");
            skipped++;
            continue;
        }

        inserted++;

        if (inserted % 100 == 0)
            Console.WriteLine($"  {inserted} rows inserted so far…");
    }
    catch (Exception ex)
    {
        errors++;
        Console.WriteLine($"Line {lineNumber}: ERROR – {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"Done! Inserted: {inserted}  |  Skipped: {skipped}  |  Errors: {errors}");

// ---------------------------------------------------------------------------
// Helper methods
// ---------------------------------------------------------------------------

/// <summary>
/// Returns the directory containing this source file (i.e., the project root).
/// The compiler fills in the CallerFilePath at build time.
/// </summary>
static string GetSourceFileDirectory(
    [System.Runtime.CompilerServices.CallerFilePath] string path = "")
    => System.IO.Path.GetDirectoryName(path)!;

static void AddParam(NpgsqlCommand cmd, string name, string value)
    => cmd.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

static void AddNullableParam<T>(NpgsqlCommand cmd, string name, T? value) where T : struct
    => cmd.Parameters.AddWithValue(name, value.HasValue ? (object)value.Value : DBNull.Value);

static short? ParseShort(string s)
    => short.TryParse(s.Trim(), out short v) ? v : null;

static int? ParseInt(string s)
    => int.TryParse(s.Trim(), out int v) ? v : null;

static decimal? ParseDecimal(string s)
    => decimal.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal v) ? v : null;

static DateOnly? ParseDate(string s)
{
    s = s.Trim();
    if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        return d;
    return null;
}

/// <summary>
/// Splits a CSV line into fields, handling quoted fields that may contain commas or newlines.
/// </summary>
static string[] SplitCsvLine(string line)
{
    var fields = new System.Collections.Generic.List<string>();
    int i = 0;

    while (i <= line.Length)
    {
        if (i == line.Length)
        {
            fields.Add(string.Empty);
            break;
        }

        if (line[i] == '"')
        {
            // Quoted field
            i++; // skip opening quote
            var sb = new System.Text.StringBuilder();
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // Escaped double-quote inside quoted field
                        sb.Append('"');
                        i += 2;
                    }
                    else
                    {
                        i++; // skip closing quote
                        break;
                    }
                }
                else
                {
                    sb.Append(line[i++]);
                }
            }
            fields.Add(sb.ToString());

            // Skip the comma separator
            if (i < line.Length && line[i] == ',') i++;
        }
        else
        {
            // Unquoted field
            int start = i;
            while (i < line.Length && line[i] != ',') i++;
            fields.Add(line[i > start ? start..i : start..i]);
            if (i < line.Length) i++; // skip comma
        }
    }

    return fields.ToArray();
}
