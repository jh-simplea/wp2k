using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data.SqlClient;
using System.Configuration;
using System.Xml.Linq;
using System.Collections.Specialized;
using System.Transactions;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace wp2k {
	class Program {
		protected static string forbiddenChars = "[^0-9a-zA-Z_-]"; // Anything that's not alphanumeric, underscore, or dash
		// Read config file.
		protected static AppSettingsReader reader = new AppSettingsReader();
		protected static NameValueCollection config = ConfigurationManager.AppSettings;
		// Read WPXML file and get relevant namespaces.
		protected static XDocument wpxml = XDocument.Load(config.Get("file"));
		protected static XNamespace wpns = "http://wordpress.org/export/1.1/";
		protected static XNamespace encoded = "http://purl.org/rss/1.0/modules/content/";
		// Some common information that doesn't like to be parsed on the fly
		protected static int blogId = Int32.Parse(config.Get("kenticoBlogId"));
		protected static int siteId = Int32.Parse(config.Get("siteId"));
		protected static int nodeOwnerId = Int32.Parse(config.Get("nodeOwnerId"));

		static void Main(string[] args) {
			using (kenticofreeEntities context = new kenticofreeEntities()) {
				ProcessAuthors(context);
				//ProcessPosts(context);
			}
		}

		/**
		 * Generate a sha2 hash the same way Kentico does.
		 * 
		 * CMS.GlobalHelper.SecurityHelper.GetSHA2Hash() and ValidationHelper.GetStringFromHash()
		 */
		private static string GetHash(string inputData) {
			SHA256Managed sh = new SHA256Managed();
			byte[] bytes = Encoding.Default.GetBytes(inputData);
			byte[] hashBytes = sh.ComputeHash(bytes);

			StringBuilder stringBuilder = new StringBuilder();
			for (int i = 0; i < hashBytes.Length; i++)
			{
				byte b = hashBytes[i];
				stringBuilder.Append(string.Format("{0:x2}", b));
			}

			return stringBuilder.ToString();
		}

		protected static void ProcessAuthors(kenticofreeEntities context)
		{
			var authors = (from a in wpxml.Descendants(wpns + "author")
						   where a.Element(wpns + "author_login").Value != "admin" // Ignore the "admin" user, since we already have an admin in Kentico (and "admin" is a restricted username)
							   select new CMS_User {
								   UserName = a.Element(wpns + "author_login").Value,
								   FirstName = a.Element(wpns + "author_first_name").Value, // CDATA element
								   MiddleName = "", // Prevent entry as "NULL"
								   LastName = a.Element(wpns + "author_last_name").Value, // CDATA element
								   FullName = a.Element(wpns + "author_display_name").Value, // CDATA element
								   Email = a.Element(wpns + "author_email").Value,
								   UserGUID = Guid.NewGuid(),
								   PreferredCultureCode = "en-US",
								   PreferredUICultureCode = "en-US",
								   UserEnabled = true,
								   UserIsEditor = true,
								   UserIsGlobalAdministrator = false,
								   UserIsDomain = false,
								   UserIsExternal = false,
								   UserIsHidden = false,
								   UserPasswordFormat = "SHA2SALT",
								   UserCreated = DateTime.Now,
								   UserLastModified = DateTime.Now
							   });

			foreach(CMS_User author in authors) {
				// I don't like blank passwords for so many users, especially since most won't ever sign in. So let's add a fairly simple password to keep the accounts from just being accessed with zero effort
				author.UserPassword = GetHash("addedByWP2k2013" + author.UserGUID);

				// Add and save to get the UserID
				context.CMS_User.AddObject(author);
				context.SaveChanges();

				// Create a user profile
				CMS_UserSettings settings = new CMS_UserSettings() {
					UserActivationDate = DateTime.Now,
					UserShowSplashScreen = true,
					UserSettingsUserGUID = author.UserGUID,
					UserSettingsUserID = author.UserID,
					UserBlogPosts = 0, // Initialize to 0 so we can increment easily
					UserWaitingForApproval = false,
					UserWebPartToolbarEnabled = true,
					UserWebPartToolbarPosition = "right",
					UserBadgeID = 43
				};

				// Connect the user with a role
				CMS_UserRole role = new CMS_UserRole() {
					UserID = author.UserID,
					RoleID = 6 // CMS Editors
				};

				// Connect the user with the site
				CMS_UserSite site = new CMS_UserSite() {
					UserID = author.UserID,
					SiteID = siteId
				};

				context.CMS_UserSettings.AddObject(settings);
				context.CMS_UserRole.AddObject(role);
				context.CMS_UserSite.AddObject(site);
				context.SaveChanges();

				// TODO: Just one for testing purposes
				break;
			}
		}

		/**
		 * The main function for importing the posts.
		 */
		protected static void ProcessPosts(kenticofreeEntities context) {
			// Gather list of Posts and make them each Entities
			var posts = (from i in wpxml.Descendants("item")
							where i.Element(wpns + "post_type").Value == "post" && i.Element(wpns + "status").Value == "publish"
							select new CONTENT_BlogPost {
								BlogPostID = Int32.Parse(i.Element(wpns + "post_id").Value),
								BlogPostTitle = i.Element("title").Value,
								BlogPostDate = DateTime.Parse(i.Element(wpns + "post_date").Value),
								BlogPostSummary = i.Element("description").Value,
								BlogPostAllowComments = true,
								BlogPostBody = i.Element(encoded + "encoded").Value, // CDATA element
								BlogLogActivity = true
							});

			// Get the blog by the ID set in the settings
			CMS_Tree blog = (from b in context.CONTENT_Blog
								join d in context.CMS_Document on b.BlogID equals d.DocumentForeignKeyValue
								join t in context.CMS_Tree on d.DocumentNodeID equals t.NodeID
								where b.BlogID == blogId && t.NodeClassID == 3423 //Blog class ID, otherwise we end up with a bunch of stuff that aren't blogs
								select t).First();

			foreach (CONTENT_BlogPost post in posts) {
				CMS_Document postDoc = ImportPost(context, blog, post);
				ImportComments(context, postDoc);

				// TODO: Break for now, so we don't have three years' worth of entries on the trial runs
				break;
			}
		}


		/**
		 * Import Post
		 * 
		 * Imports a given post into the database, creating the necessary dependencies,
		 * including BlogMonth (if it doesn't already exist), TreeNode, and Document.
		 * Returns the newly-created document that represents the post.
		 * 
		 * @param entities context
		 * @param CMS_Tree blog
		 * @param CONTENT_BlogPost post
		 * @return CMS_Document
		 */
		protected static CMS_Document ImportPost(kenticofreeEntities context, CMS_Tree blog, CONTENT_BlogPost post) {
			Regex forbidden = new Regex(forbiddenChars);

			/* We want to preserve the IDs of the Posts, but EF won't let us turn on IDENTITY_INSERT
			* with its available methods (ExecuteStoreCommand and SaveChanges are different connections, it seems). 
			* So we have to do it the old fashioned way.
			*/
			object[] values = new object[]{
							post.BlogPostID.ToString(),
							post.BlogPostTitle,
							post.BlogPostDate.Date.ToString("yyyy-MM-dd HH:mm:ss"),
							post.BlogPostSummary,
							post.BlogPostAllowComments,
							post.BlogPostBody,
							post.BlogLogActivity
						};

			/* We'll use MERGE here, so that we can handle existing entries.
			 * The "xmlTrumpsDb" config switch will allow a choice between nuking what's in the DB
			 * or preserving it.
			 */
			string cmd = "SET IDENTITY_INSERT CONTENT_BlogPost ON; ";
			cmd += "MERGE CONTENT_BlogPost ";
			cmd += "USING (VALUES ({0},{1},{2},{3},{4},{5},{6})) as temp(BlogPostID, BlogPostTitle, BlogPostDate, BlogPostSummary, BlogPostAllowComments, BlogPostBody, BlogLogActivity) ";
			cmd += "ON CONTENT_BlogPost.BlogPostID = temp.BlogPostID ";
			// To nuke or not to nuke, that is the question...
			if (config.Get("xmlTrumpsDb") == "true") {
				cmd += "WHEN MATCHED THEN ";
				cmd += "UPDATE SET CONTENT_BlogPost.BlogPostTitle = temp.BlogPostTitle, CONTENT_BlogPost.BlogPostDate = temp.BlogPostDate, CONTENT_BlogPost.BlogPostSummary = temp.BlogPostSummary, CONTENT_BlogPost.BlogPostAllowComments = temp.BlogPostAllowComments, CONTENT_BlogPost.BlogPostBody = temp.BlogPostBody, CONTENT_BlogPost.BlogLogActivity = temp.BlogLogActivity ";
			}
			cmd += "WHEN NOT MATCHED THEN ";
			cmd += "INSERT (BlogPostId, BlogPostTitle, BlogPostDate, BlogPostSummary, BlogPostAllowComments, BlogPostBody, BlogLogActivity) VALUES ({0},{1},{2},{3},{4},{5},{6}); ";
			cmd += "SET IDENTITY_INSERT CONTENT_BlogPost OFF;";
			context.ExecuteStoreCommand(cmd, values);

			// See if there's a BlogMonth entry for the month this post is for
			CMS_Tree month = GetBlogMonth(context, post, blog);

			CMS_Class blogClass = context.CMS_Class.Where(x => x.ClassName == "CMS.BlogPost").First();

			CMS_Tree treeNode = (from t in context.CMS_Tree
								 join d in context.CMS_Document on t.NodeID equals d.DocumentNodeID
								 where d.DocumentForeignKeyValue == post.BlogPostID && t.NodeClassID == 3423
								 select t).FirstOrDefault();

			// Add a new node only if one doesn't already exist
			if (treeNode == null) {
				// Create the Tree Node for the post and add it in
				treeNode = new CMS_Tree() {
					NodeAliasPath = string.Format("{0}/{1}", month.NodeAliasPath, forbidden.Replace(post.BlogPostTitle, "-")),
					NodeName = post.BlogPostTitle,
					NodeAlias = forbidden.Replace(post.BlogPostTitle, "-"),
					NodeClassID = blogClass.ClassID,
					NodeParentID = month.NodeID,
					NodeLevel = Int32.Parse(config.Get("kenticoBlogLevel")) + 2,
					NodeACLID = 1, // Default ACL ID
					NodeSiteID = siteId,
					NodeGUID = Guid.NewGuid(),
					NodeOwner = nodeOwnerId,
					NodeInheritPageLevels = "",
					NodeTemplateForAllCultures = true
				};
				context.CMS_Tree.AddObject(treeNode);
				treeNode.NodeOrder = GetNodeOrder(context, treeNode);
				month.NodeChildNodesCount++; // Increment the child nodes count, so the new post will display in the CMS
				context.SaveChanges();
			}

			// Create the document and add it into the database
			CMS_Document postDoc = AddDocument(context, treeNode, post.BlogPostID);

			return postDoc;
		}

		/**
		 * Import Comments
		 * 
		 * Comments aren't as involved as Posts, so the loop to import can be self-contained. This function
		 * imports the comments associated with the given post.
		 * 
		 * The Wordpress export doesn't distinguish between spam comments and comments that simply haven't yet
		 * been approved, so we can't explicitly mark them as spam in Kentico. We can, though, retain Wordpress'
		 * behavior and just not approve them, thus retaining the same look on the Kentico blog as we had in WP.
		 * 
		 * Wordpress also tracks ping/trackbacks differently than Kentico. Kentico just keeps a simple boolean value
		 * for "IsTrackBack" and doesn't appear to distinguish between trackback and pingback, while Wordpress uses a
		 * string value with null, Comment, Pingback, and Trackback as potential values
		 * 
		 * @param entity context
		 * @param CMS_Document postDoc
		 */
		protected static void ImportComments(kenticofreeEntities context, CMS_Document postDoc) {
			// Get the comments from the XML
			var comments = (from c in wpxml.Descendants("item").Where(x => x.Element(wpns + "post_id").Value == postDoc.DocumentForeignKeyValue.ToString()).Descendants(wpns + "comment")
							select new Blog_Comment {
								CommentID = Int32.Parse(c.Element(wpns + "comment_id").Value),
								CommentUserName = c.Element(wpns + "comment_author").Value,
								CommentEmail = c.Element(wpns + "comment_author_email").Value,
								CommentUrl = c.Element(wpns + "comment_author_url").Value,
								CommentApproved = (c.Element(wpns + "comment_approved").Value != "0"), // Convert "0"/"1" to false/true
								CommentDate = DateTime.Parse(c.Element(wpns + "comment_date").Value),
								CommentText = c.Element(wpns + "comment_content").Value,
								CommentIsTrackBack = (!String.IsNullOrEmpty(c.Element(wpns + "comment_type").Value) && c.Element(wpns + "comment_type").Value != "Comment"), // if comment_type has a value (not NullOrEmpty), AND that value is not "Comment", then it's a ping/trackback; otherwise it's just a comment
								CommentPostDocumentID = postDoc.DocumentID
							}
			);

			// Insert them into the database
			foreach (Blog_Comment comment in comments) {
				context.Blog_Comment.AddObject(comment);
			}

			context.SaveChanges();
		}

		/**
		 * Find or create the "BlogMonth" entry for the month a given post is in.
		 * 
		 * Returns a TreeNode of the requested month, either by finding an existing one or creating a new one.
		 * 
		 * @param entity context
		 * @param CONTNET_BlogPost post
		 * @param CMS_Tree blog
		 * @return CMS_Tree
		 */
		protected static CMS_Tree GetBlogMonth(kenticofreeEntities context, CONTENT_BlogPost post, CMS_Tree blog) {

			Regex forbidden = new Regex(forbiddenChars);

			// Does one exist?
			var monthQuery = (from m in context.CONTENT_BlogMonth
							  where m.BlogMonthStartingDate.Year == post.BlogPostDate.Year && m.BlogMonthStartingDate.Month == post.BlogPostDate.Month
							  select m);

			CMS_Tree treeNode = new CMS_Tree();

			// If not, make a new one
			if (monthQuery.Any() == false) {
				CONTENT_BlogMonth month = new CONTENT_BlogMonth() {
					BlogMonthName = post.BlogPostDate.ToString("MMMM yyyy"),
					BlogMonthStartingDate = new DateTime(post.BlogPostDate.Year, post.BlogPostDate.Month, 01)
				};

				context.CONTENT_BlogMonth.AddObject(month);

				// Found out the classID of the CMS.BlogMonth document type
				CMS_Class monthClass = context.CMS_Class.Where(x => x.ClassName == "CMS.BlogMonth").First();

				// Add the corresponding tree node
				treeNode = new CMS_Tree() {
					NodeAliasPath = string.Format("{0}/{1}/{2}", config.Get("kenticoBlogPath"), forbidden.Replace(blog.NodeName, "-"), forbidden.Replace(month.BlogMonthName, "-")),
					NodeTemplateForAllCultures = true,
					NodeName = month.BlogMonthName,
					NodeAlias = forbidden.Replace(month.BlogMonthName, "-"),
					NodeClassID = monthClass.ClassID,
					NodeParentID = blog.NodeID,
					NodeLevel = Int32.Parse(config.Get("kenticoBlogLevel")) + 1,
					NodeACLID = 1, // Default ACL ID
					NodeSiteID = Int32.Parse(config.Get("siteId")),
					NodeGUID = Guid.NewGuid(),
					NodeOwner = Int32.Parse(config.Get("nodeOwnerId")),
					NodeInheritPageLevels = "",
					NodeChildNodesCount = 0 // Start out with a number, instead of NULL, so we can easily increment it
				};

				treeNode.NodeOrder = GetNodeOrder(context, treeNode);

				context.CMS_Tree.AddObject(treeNode);
				context.SaveChanges();

				AddDocument(context, treeNode, month.BlogMonthID);
			}
			// If so, use it
			else {
				CONTENT_BlogMonth month = monthQuery.First();
				treeNode = (from t in context.CMS_Tree
							join d in context.CMS_Document on t.NodeID equals d.DocumentNodeID
							join b in context.CONTENT_BlogMonth on d.DocumentForeignKeyValue equals b.BlogMonthID
							where b.BlogMonthID == month.BlogMonthID
							select t).Single();
			}

			return treeNode;
		}

		/**
		 * Add a document based on the given treeNode and related item
		 * 
		 * @param entity context
		 * @param CMS_Tree treeNode
		 * @param int itemId
		 * @return CMS_Document
		 */
		protected static CMS_Document AddDocument(kenticofreeEntities context, CMS_Tree treeNode, int itemId) {
			// Create the document and add it into the database
			CMS_Document doc = new CMS_Document() {
				DocumentName = treeNode.NodeName,
				DocumentNamePath = treeNode.NodeAliasPath,
				DocumentForeignKeyValue = itemId,
				DocumentCulture = "en-US",
				DocumentShowInSiteMap = true,
				DocumentMenuItemHideInNavigation = false,
				DocumentUseNamePathForUrlPath = false,
				DocumentStylesheetID = -1,
				DocumentNodeID = treeNode.NodeID,
				DocumentGUID = Guid.NewGuid(),
				DocumentWorkflowCycleGUID = Guid.NewGuid(),
				DocumentCreatedByUserID = treeNode.NodeOwner,
				DocumentCreatedWhen = DateTime.Now,
				DocumentModifiedWhen = DateTime.Now,
				DocumentModifiedByUserID = treeNode.NodeOwner,
				DocumentUrlPath = "",
				DocumentContent = "",
				DocumentIsArchived = false
			};

			context.CMS_Document.AddObject(doc);
			context.SaveChanges();

			return doc;
		}

		/**
		 * Find out what position a given node should be in to show up as the most recent
		 * 
		 * @param entity context
		 * @param CMS_Tree tree
		 * @return int
		 */
		protected static int GetNodeOrder(kenticofreeEntities context, CMS_Tree tree) {
			int nodeOrder = 1;
			CMS_Tree lastNode = (from node in context.CMS_Tree
								 where node.NodeParentID == tree.NodeParentID
								 orderby node.NodeOrder descending
								 select node).FirstOrDefault();

			if (lastNode != null) {
				nodeOrder = (int)lastNode.NodeOrder;
			}

			return nodeOrder;
		}
	}
}