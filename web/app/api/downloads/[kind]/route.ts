const downloads = {
  manager:
    "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.28/ComoteManager_Setup.exe",
  client:
    "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.28/ComoteClient_Setup.exe",
} as const;

export async function GET(
  _request: Request,
  context: { params: Promise<{ kind: string }> },
) {
  const { kind } = await context.params;
  if (!(kind in downloads)) {
    return Response.json({ error: "Unknown package" }, { status: 404 });
  }
  return Response.redirect(
    downloads[kind as keyof typeof downloads],
    307,
  );
}
