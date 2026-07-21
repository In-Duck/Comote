const downloads = {
  manager: "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.18/ComoteManager-1.6.0-preview.18-win-x64.zip",
  client: "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.18/ComoteClient-1.6.0-preview.18-win-x64.zip",
} as const;

export async function GET(
  _request: Request,
  context: { params: Promise<{ kind: string }> },
) {
  const { kind } = await context.params;
  if (!(kind in downloads)) {
    return Response.json({ error: "Unknown package" }, { status: 404 });
  }
  return Response.redirect(downloads[kind as keyof typeof downloads], 307);
}