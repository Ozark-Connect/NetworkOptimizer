#!/bin/bash
# Run tests with code coverage and generate HTML report
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
COVERAGE_DIR="$PROJECT_ROOT/coverage"

cd "$PROJECT_ROOT"

# Clean previous coverage
rm -rf "$COVERAGE_DIR"
mkdir -p "$COVERAGE_DIR"

echo "Running tests with coverage..."
dotnet test \
    --collect:"XPlat Code Coverage" \
    --results-directory "$COVERAGE_DIR" \
    --settings "$SCRIPT_DIR/coverage.runsettings"

# Find all coverage files
COVERAGE_FILES=$(find "$COVERAGE_DIR" -name "coverage.cobertura.xml" | tr '\n' ';' | sed 's/;$//')

if [ -z "$COVERAGE_FILES" ]; then
    echo "Error: No coverage files generated"
    exit 1
fi

echo ""
echo "Coverage files found:"
find "$COVERAGE_DIR" -name "coverage.cobertura.xml"

# Try to generate HTML report if reportgenerator is available
if command -v reportgenerator &> /dev/null; then
    echo ""
    echo "Generating HTML report..."
    reportgenerator \
        -reports:"$COVERAGE_FILES" \
        -targetdir:"$COVERAGE_DIR/report" \
        -reporttypes:"Html;TextSummary"

    echo ""
    echo "=== Coverage Summary ==="
    cat "$COVERAGE_DIR/report/Summary.txt" 2>/dev/null || true
    echo ""
    echo "HTML report: $COVERAGE_DIR/report/index.html"
else
    echo ""
    echo "=== Coverage Summary (first file) ==="
    FIRST_FILE=$(find "$COVERAGE_DIR" -name "coverage.cobertura.xml" | head -1)
    grep -E "line-rate|branch-rate" "$FIRST_FILE" | head -1 | \
        sed 's/.*line-rate="\([^"]*\)".*branch-rate="\([^"]*\)".*/Line: \1, Branch: \2/' | \
        awk -F',' '{
            split($1, l, ": ");
            split($2, b, ": ");
            printf "Line Coverage:   %.1f%%\n", l[2] * 100;
            printf "Branch Coverage: %.1f%%\n", b[2] * 100;
        }'

    echo ""
    echo "To get a merged report, install ReportGenerator:"
    echo "  dotnet tool install -g dotnet-reportgenerator-globaltool"
fi
