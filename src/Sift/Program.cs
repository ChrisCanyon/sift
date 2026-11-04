using Sift;

return args.FirstOrDefault() switch
{
    "hunks" => await HunksCommand.RunAsync(args[1..]),
    "reply" => await ThreadCommands.ReplyAsync(args[1..]),
    "threads" => await ThreadCommands.ListAsync(args[1..]),
    _ => await ServerCommand.RunAsync(args),
};
