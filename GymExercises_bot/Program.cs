using Microsoft.Data.Sqlite;
using Telegram.BotAPI;
using Telegram.BotAPI.AvailableMethods;
using Telegram.BotAPI.AvailableTypes;
using Telegram.BotAPI.GettingUpdates;
using System.Text.Json.Nodes;
using Telegram.BotAPI.UpdatingMessages;

var bot = new TelegramBotClient(Environment.GetEnvironmentVariable("GYMBOT_TOKEN"));
string msg;
long chatId;
Dictionary<long, State?> chatState = new Dictionary<long, State?>();

//long polling
var updates = bot.GetUpdates();
while (true) {
	if (updates.Any()) {
		foreach (var update in updates) {
			//getting the message and chat id of the user
			msg = update?.Message?.Text ?? update.CallbackQuery.Data;
			chatId = update?.Message?.Chat.Id ?? update.CallbackQuery.Message.Chat.Id;

			//to prevent exception
			if (!chatState.ContainsKey(chatId))
				chatState[chatId] = null;

			if (update.Message is not null) { //if the update is a message and not a callback query
				if (msg == "/search") { //command /search
					chatState[chatId] = State.NameExercise;
					bot.SendMessage(chatId, "Which exercise are you looking for?");
				} else if (msg == "/help") { //command /help
					chatState[chatId] = null;
					HelpCommand(chatId);
				} else if (msg == "/favourites") { //command /favourites
					FindFavourites(chatId, update.Message.From.Id.ToString());
				} else if (chatState[chatId] == State.NameExercise) { //the user typed the exercise's name 
					CommandSearchExercises(chatId, msg);
				} else if (msg == "/start") { //command /start
					StartCommand(chatId);
				}
			} else if (update.CallbackQuery is not null) {//if the update is a callback query and not a message
				if (chatState[chatId] == State.Exercise) { //the user clicked on the exercise in the inline keyboard
					bot.DeleteMessageAsync(chatId, update.CallbackQuery.Message.MessageId);
					CommandSearchQueryExercise(chatId, msg, update.CallbackQuery.From.Id);
				} else { //the user clicked on "add to favourite" or "remove from favourite" in the inline keyboard on the exercise's message
					AddOrRemoveFavourites(chatId.ToString(), msg, update.CallbackQuery.From.Id.ToString(), update.CallbackQuery.Message.MessageId);
				}
			}
		}

		//getting the next update
		var offset = updates.Last().UpdateId + 1;
		updates = bot.GetUpdates(offset);
	} else {
		updates = bot.GetUpdates();
	}

}

//Search the exercises using the name provided by the user
async Task CommandSearchExercises(long chatId, string nameExercise) {
	List<List<InlineKeyboardButton>> inlineKeyboardButtons = new List<List<InlineKeyboardButton>>(); //buttons for inline keyboards

	using (var db = new SqliteConnection($"Data Source='{Environment.GetEnvironmentVariable("GYMBOT_DB")}'")) {
		db.Open();

		var command = db.CreateCommand();
		command.CommandText = @"SELECT name, id
						FROM exercise
						WHERE LOWER(name) LIKE $name";
		command.Parameters.AddWithValue("$name", "%" + nameExercise + "%"); //the characters '%' are for the LIKE operator in the query

		//build the buttons for the inline keyboards, using the exercise's id and name
		var reader = command.ExecuteReader();
		while (reader.Read()) {
			InlineKeyboardButton button = new InlineKeyboardButton((string)reader["name"]);
			button.CallbackData = (string)reader["id"];

			inlineKeyboardButtons.Add([button]);
		}

		InlineKeyboardMarkup inlineKeyboardMarkup = new(inlineKeyboardButtons);

		bot.SendMessage(chatId, "Click on the right exercise:", replyMarkup: inlineKeyboardMarkup);
		reader.Close();
		chatState[chatId] = State.Exercise;
	}
}

//Find the exercise choosed by the user from the inline keyboards and sends to him the photos and the message
async Task CommandSearchQueryExercise(long chatId, string idExercise, long idUser) {
	List<List<InlineKeyboardButton>> buttons = new List<List<InlineKeyboardButton>>(); //for the buttons Add to favourites / Remove from favourites

	using (var db = new SqliteConnection($"Data Source='{Environment.GetEnvironmentVariable("GYMBOT_DB")}'")) {
		db.Open();

		var command = db.CreateCommand();
		command.CommandText = @"
						SELECT *
						FROM exercise
						WHERE id = $id
					";
		command.Parameters.AddWithValue("id", idExercise);

		var reader = command.ExecuteReader();
		string tmp = "";
		string msg = "";
		string id = "";

		JsonArray images = new JsonArray();

		//build the message
		while (reader.Read()) {
			id = (string)reader["id"];
			msg = $"*NAME:* {reader["name"]}\n" +
				$"*FORCE:* {reader["force"]}\n" +
				$"*LEVEL:* {reader["level"]}\n" +
				$"*EQUIPMENT:* {reader["equipment"]}\n" +
				$"*CATEGORY:* {reader["category"]}\n";

			//calculatint string for PrimaryMuscles from json array
			JsonArray arr = (JsonArray)JsonArray.Parse((string)reader["primaryMuscles"]);
			foreach (string str in arr) {
				tmp += str + " ";
			}
			msg += $"*PRIMARY MUSCLES:* {tmp}\n";

			tmp = "";
			//calculating string for instructions
			arr = (JsonArray)JsonArray.Parse((string)reader["instructions"]);
			foreach (string str in arr) {
				tmp += str + "\n";
			}
			msg += $"*INSTRUCTIONS:*\n{tmp}";

			images = (JsonArray)JsonArray.Parse((string)reader["images"]);
		}

		//check if the exercise is in the favourites of the user
		command = db.CreateCommand();
		command.CommandText = @"
						SELECT *
						FROM favourites
						WHERE exercise_id = $id AND user = $user
					";
		command.Parameters.AddWithValue("id", id);
		command.Parameters.AddWithValue("user", idUser.ToString());

		reader = command.ExecuteReader();
		StateFavourites obj;

		if (reader.HasRows) {
			InlineKeyboardButton button = new InlineKeyboardButton("Remove from favourites");
			button.CallbackData = id;
			buttons.Add([button]);
		} else {
			InlineKeyboardButton button = new InlineKeyboardButton("Add to favourites");
			button.CallbackData = id;
			buttons.Add([button]);
		}
		InlineKeyboardMarkup markup = new(buttons);

		//load the photos
		InputMediaPhoto[] media = [
			new InputMediaPhoto("attach://img1"),
			new InputMediaPhoto("attach://img2"),
		];
		string path = Path.Combine(Environment.GetEnvironmentVariable("GYMBOT_IMG_PATH"), id);
		InputFile img1 = new InputFile(await System.IO.File.ReadAllBytesAsync(Path.Combine(path, "0.jpg")), "0.jpg");
		InputFile img2 = new InputFile(await System.IO.File.ReadAllBytesAsync(Path.Combine(path, "1.jpg")), "1.jpg");


		Dictionary<string, InputFile> files = new Dictionary<string, InputFile>();
		files.Add("img1", img1);
		files.Add("img2", img2);

		SendMediaGroupArgs args = new SendMediaGroupArgs(chatId, media);
		args.Files = files;

		await bot.SendMediaGroupAsync(args);
		bot.SendMessageAsync(chatId, msg, parseMode: "markdown", replyMarkup: markup);
		reader.Close();

		chatState[chatId] = null;
	}
}

// /help command
async Task HelpCommand(long chatId) {
	string msg = "Commands available:\n" +
		"- /search - search the instructions of an exercise (/search barbell bench press).\n" +
		"- /favourites - list favourite exercise.";

	bot.SendMessageAsync(chatId, msg);
}

// /help command
async Task StartCommand(long chatId) {
	string msg = "Welcome to Gym Exercises bot!\n" +
		"Using the '/search' command you can find the information and instructions of an exercises.\n" +
		"You can add an exercise to your favourites list clicking on the button inside the inline keyboard of the exercise's message.\n" +
		"To retrieve the list of your favourite exercises you can use the '/favourites' command.";

	bot.SendMessageAsync(chatId, msg);
}

//Find the favourite exercise of the user
async Task FindFavourites(long chatId, string idUser) {
	List<List<InlineKeyboardButton>> buttons = new();

	using (var db = new SqliteConnection($"Data Source='{Environment.GetEnvironmentVariable("GYMBOT_DB")}'")) {
		db.Open();

		string msg;
		var command = db.CreateCommand();
		command.CommandText = @"SELECT * FROM favourites WHERE user = $user";
		command.Parameters.AddWithValue("user", idUser);

		var reader = command.ExecuteReader();

		while (reader.Read()) {
			var command2 = db.CreateCommand();
			command2.CommandText = @"SELECT name FROM exercise WHERE id = $id";
			command2.Parameters.AddWithValue("id", reader["exercise_id"]);

			var reader2 = command2.ExecuteReader();
			reader2.Read();
			InlineKeyboardButton exercise = new InlineKeyboardButton((string)reader2["name"]);
			exercise.CallbackData = (string)reader["exercise_id"];
			buttons.Add([exercise]);
		}

		InlineKeyboardMarkup markup = new(buttons);

		if (buttons.Count == 0) {
			msg = "You don't have a favourite exercise.";
		} else {
			msg = "Here is your favorites exercise:";
			chatState[chatId] = State.Exercise;
		}


		bot.SendMessageAsync(chatId, msg, replyMarkup: markup);

	}
}

async Task AddOrRemoveFavourites(string chatId, string idExercise, string userId, int messageId) {
	string msg;
	List<List<InlineKeyboardButton>> buttons = new List<List<InlineKeyboardButton>>(); //to change the inline keyboard

	using (var db = new SqliteConnection($"Data Source='{Environment.GetEnvironmentVariable("GYMBOT_DB")}'")) {
		db.Open();
		var command = db.CreateCommand();
		command.CommandText = @"SELECT * 
								FROM favourites
								WHERE user = $user AND exercise_id = $exercise";

		command.Parameters.AddWithValue("user", userId);
		command.Parameters.AddWithValue("exercise", idExercise);

		var reader = command.ExecuteReader();

		//adding or removing the exercise from the favourites list of the user
		command = db.CreateCommand();
		if (!reader.HasRows) {
			InlineKeyboardButton removeFromFavourite = new InlineKeyboardButton("Remove from favourite");
			removeFromFavourite.CallbackData = idExercise;
			buttons.Add([removeFromFavourite]);
			command.CommandText = "INSERT INTO favourites VALUES ($userId, $idExercise)";
			msg = "Exercises added to favourites";
		} else {
			InlineKeyboardButton addToFavourite = new InlineKeyboardButton("Add to favourite");
			addToFavourite.CallbackData = idExercise;
			buttons.Add([addToFavourite]);
			command.CommandText = "DELETE FROM favourites WHERE user = $userId AND exercise_id = $idExercise";
			msg = "Exercises deleted from favourites";
		}

		command.Parameters.AddWithValue("userId", userId);
		command.Parameters.AddWithValue("idExercise", idExercise);

		await command.ExecuteNonQueryAsync();

		InlineKeyboardMarkup markup = new InlineKeyboardMarkup(buttons);
		await bot.EditMessageReplyMarkupAsync(long.Parse(chatId), messageId, replyMarkup: markup); //change the inline keyboard on the exercise's message
		bot.SendMessageAsync(chatId, msg);
	}
}

enum State {
	NameExercise,
	Exercise
};

public class StateFavourites {
	public ActionFavourites actionState { get; set; }
	public string idExercise { get; set; }
}

public enum ActionFavourites {
	ADD,
	REMOVE
}

