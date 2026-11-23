# Shared GitHub Actions workflow_dispatch transport.
# Avoid CLI positional-argument parsing differences for JSON request bodies.
dispatch_workflow() {
  local repository="${1:?repository is required}"
  local workflow_ref="${2:?workflow reference is required}"
  local payload="${3:?workflow payload is required}"
  local token="${GH_TOKEN:?GH_TOKEN is required}"
  local api_url="${GITHUB_API_URL:-https://api.github.com}"
  local response_file
  local status

  response_file="$(mktemp)"
  if ! status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' \
    --connect-timeout 10 --max-time 30 \
    --request POST \
    --header "Authorization: Bearer $token" \
    --header "Accept: application/vnd.github+json" \
    --header "X-GitHub-Api-Version: 2022-11-28" \
    --header "Content-Type: application/json" \
    --data-binary "$payload" \
    "${api_url%/}/repos/${repository}/actions/workflows/${workflow_ref}/dispatches")"; then
    cat "$response_file" >&2 || true
    rm -f "$response_file"
    echo "::error::workflow_dispatch transport failed for $workflow_ref" >&2
    return 1
  fi

  if [[ "$status" != "204" ]]; then
    cat "$response_file" >&2 || true
    rm -f "$response_file"
    echo "::error::workflow_dispatch failed for $workflow_ref (HTTP $status)" >&2
    return 1
  fi

  rm -f "$response_file"
  echo "::notice::workflow_dispatch accepted workflow=$workflow_ref"
}


# Write a JSON REST resource and return the response body. Used for durable
# comment updates as well as workflow dispatch; never rely on gh's form parser
# for multiline Markdown bodies.
github_api_write() {
  local method="${1:?HTTP method is required}"
  local endpoint="${2:?API endpoint is required}"
  local payload="${3:?JSON payload is required}"
  local token="${GH_TOKEN:?GH_TOKEN is required}"
  local api_url="${GITHUB_API_URL:-https://api.github.com}"
  local response_file
  local status

  response_file="$(mktemp)"
  if ! status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' \
    --connect-timeout 10 --max-time 30 \
    --request "$method" \
    --header "Authorization: Bearer $token" \
    --header "Accept: application/vnd.github+json" \
    --header "X-GitHub-Api-Version: 2022-11-28" \
    --header "Content-Type: application/json" \
    --data-binary "$payload" \
    "${api_url%/}/${endpoint}")"; then
    cat "$response_file" >&2 || true
    rm -f "$response_file"
    echo "::error::GitHub REST write transport failed: $method $endpoint" >&2
    return 1
  fi

  case "$status" in
    200|201|202|204) ;;
    *)
      cat "$response_file" >&2 || true
      rm -f "$response_file"
      echo "::error::GitHub REST write failed: $method $endpoint (HTTP $status)" >&2
      return 1
      ;;
  esac

  cat "$response_file"
  rm -f "$response_file"
}
