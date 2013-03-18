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
using System.Data.Objects;

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
		protected static XNamespace dc = "http://purl.org/dc/elements/1.1/";
		// Some common information that doesn't like to be parsed on the fly
		protected static int blogId = Int32.Parse(config.Get("kenticoBlogId"));
		protected static int siteId = Int32.Parse(config.Get("siteId"));
		protected static int nodeOwnerId = Int32.Parse(config.Get("nodeOwnerId"));

		static void Main(string[] args) {
			using (kenticofreeEntities context = new kenticofreeEntities()) {
				ProcessTags(context);
				ProcessCategories(context);
				ProcessAuthors(context);
				ProcessPosts(context);
			}
		}

		/**
		 * Add tags to the database.
		 * 
		 * @param entity context
		 */
		protected static void ProcessTags(kenticofreeEntities context) {
			Console.WriteLine("Adding tags to database.");

			CMS_TagGroup tg = GetTagGroup(context);
			var tags = (from t in wpxml.Descendants(wpns + "tag")
						select new CMS_Tag {
							TagName = t.Element(wpns + "tag_slug").Value,
							TagGroupID = tg.TagGroupID,
							TagCount = 0
						});

			foreach (CMS_Tag tag in tags) {
				// Don't add an existing tag
				var exists = (from t in context.CMS_Tag
							  where t.TagName == tag.TagName && t.TagGroupID == tag.TagGroupID
							  select t
							);
				if (!exists.Any()) {
					context.CMS_Tag.AddObject(tag);
				}
			}

			context.SaveChanges();
		}

		/**
		 * Add categories into the database.
		 * 
		 * @param entity context
		 */
		protected static void ProcessCategories(kenticofreeEntities context) {
			Console.WriteLine("Adding categories to database.");
			var categories = (from c in wpxml.Descendants(wpns + "category")
							  select new CMS_Category {
								  CategoryCount = 0,
								  CategoryDisplayName = c.Element(wpns + "cat_name").Value,
								  CategoryName = c.Element(wpns + "category_nicename").Value,
								  CategoryDescription = c.Element(wpns + "cat_name").Value,
								  CategoryEnabled = true,
								  CategoryGUID = Guid.NewGuid(),
								  CategoryLastModified = DateTime.Now,
								  CategorySiteID = Int32.Parse(config.Get("siteId")),
								  CategoryNamePath = "/" + c.Element(wpns + "category_nicename").Value,
								  CategoryLevel = 0,
								  CategoryParentID = null
							  });
			
			CMS_Category lastCat = null;
			// TODO: Add category nesting (not doing it right now, because our export doesn't nest categories)
			foreach (CMS_Category cat in categories) {
				// Make sure we don't add a category that already exists
				var exists = (from c in context.CMS_Category
							  where c.CategoryName == cat.CategoryName
							  select c
							);
				
				if (!exists.Any()) {
					if (lastCat == null) {
						lastCat = (from o in context.CMS_Category
								   where o.CategorySiteID == cat.CategorySiteID && o.CategoryLevel == cat.CategoryLevel
								   orderby o.CategoryOrder descending
								   select o
								).FirstOrDefault();
						if (lastCat == null) {
							lastCat = new CMS_Category() { CategoryOrder = 0 };
						}
					}

					cat.CategoryOrder = lastCat.CategoryOrder + 1;

					/* We can't make the IDPath until we get an ID, and I can't find how Kentico does this, 
					 * so we have to do it the long way. */
					context.CMS_Category.AddObject(cat);
					context.SaveChanges(); // Add the category
					// Bulid the path and update our category
					cat.CategoryIDPath = "/" + cat.CategoryID.ToString().PadLeft(8, '0');
					context.SaveChanges();

					// Let's save the last inserted category, so we don't have to access the database so much
					lastCat = cat;
				}
			}
		}

		/**
		 * Get the tag group.
		 * 
		 * Kentico encourages grouping tags. Additionally, tags aren't just used for blog posts.
		 * Therefore, we're going to use or create a tag group specifically for our blog import.
		 * This group name is set in the config, so it can be an existing one. It doesn't have to
		 * exist, though, and if it doesn't, this script will create it.
		 * 
		 * @param entity context
		 * @return CMS_TagGroup
		 */
		protected static CMS_TagGroup GetTagGroup(kenticofreeEntities context) {
			string groupName = config.Get("tagGroupName");
			var tg = (from t in context.CMS_TagGroup
								where t.TagGroupName == groupName
								select t
							);

			if (tg.Any()) {
				return (CMS_TagGroup)tg.First();
			}
			else {
				Regex forbidden = new Regex(forbiddenChars);
				CMS_TagGroup group = new CMS_TagGroup() {
					TagGroupName = forbidden.Replace(groupName, ""),
					TagGroupDisplayName = groupName,
					TagGroupDescription = groupName,
					TagGroupGUID = Guid.NewGuid(),
					TagGroupSiteID = Int32.Parse(config.Get("siteId")),
					TagGroupIsAdHoc = false,
					TagGroupLastModified = DateTime.Now
				};

				context.CMS_TagGroup.AddObject(group);
				context.SaveChanges();
				return group;
			}
		}

		/**
		 * Create users based on the authors listed in the XML.
		 * 
		 * Users are required to retain the "multiple authors" capability, otherwise we lose the
		 * ability to discern who wrote what. Additionally, it allows us to create user accounts for
		 * everyone enmasse. The new accounts are given a dummy password, since not all accounts are
		 * active anymore, and therefore would never have a password set if we didn't do it.
		 * 
		 * This function currently assumes that users have not yet been added.
		 * 
		 * @param entity context
		 */
		protected static void ProcessAuthors(kenticofreeEntities context)
		{
			Console.WriteLine("Adding authors to database.");
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
								   //UserIsEditor = true,
								   UserIsEditor = false,
								   UserIsGlobalAdministrator = false,
								   UserIsDomain = false,
								   UserIsExternal = false,
								   UserIsHidden = false,
								   UserPasswordFormat = "SHA2SALT",
								   UserCreated = DateTime.Now,
								   UserLastModified = DateTime.Now
							   });

			foreach (CMS_User author in authors) {
				var exists = (from a in context.CMS_User
							  where a.UserName == author.UserName
							  select a
							);

				// Don't add ones that already exist
				if (!exists.Any()) {
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
						//RoleID = 6 // CMS Editors
						RoleID = 2 // CMS Basic User (using this for testing, because the free version doesn't allow for more editors and will completely explode if we add more)
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
				}
			}
		}

		/**
		 * The main function for importing the posts.
		 * 
		 * @param entity context
		 */
		protected static void ProcessPosts(kenticofreeEntities context) {
			Console.WriteLine("Adding posts to database.");
			// Gather list of Posts and make them each Entities
			var posts = (from i in wpxml.Descendants("item")
							where i.Element(wpns + "post_type").Value == "post" && i.Element(wpns + "status").Value == "publish"
							select new CONTENT_BlogPost {
								BlogPostID = Int32.Parse(i.Element(wpns + "post_id").Value),
								BlogPostTitle = i.Element("title").Value,
								BlogPostDate = DateTime.Parse(i.Element(wpns + "post_date").Value),
								BlogPostSummary = (String.IsNullOrEmpty(i.Element("description").Value) ? i.Element("title").Value : i.Element("description").Value),
								BlogPostAllowComments = true,
								BlogPostBody = i.Element(encoded + "encoded").Value.Replace("\n", "<br/>"), // CDATA element
								BlogLogActivity = true
							});

			// Get the blog by the ID set in the settings
			CMS_Tree blog = (from b in context.CONTENT_Blog
								join d in context.CMS_Document on b.BlogID equals d.DocumentForeignKeyValue
								join t in context.CMS_Tree on d.DocumentNodeID equals t.NodeID
								where b.BlogID == blogId && t.NodeClassID == 3423 //Blog class ID, otherwise we end up with a bunch of stuff that aren't blogs
								select t).FirstOrDefault();
			if (blog == null) {
				Console.WriteLine("No blog with the ID of " + blogId + " detected. Adding new blog.");
				CONTENT_Blog b = new CONTENT_Blog() {
							BlogName = "BlogImport",
							BlogDescription = "The Imported Blog",
							BlogOpenCommentsFor = "-1",
							BlogEnableTrackbacks = true
				};

				CMS_Class blogClass = context.CMS_Class.Where(x => x.ClassName == "CMS.BlogPost").First();
				CMS_Tree root = context.CMS_Tree.Where(x => x.NodeClassID == 1095).Where(x => x.NodeSiteID == siteId).First();

				blog = new CMS_Tree() {
					NodeAliasPath = "/" + b.BlogName,
					NodeName = b.BlogName,
					NodeAlias = b.BlogName,
					NodeClassID = blogClass.ClassID,
					NodeLevel = Int32.Parse(config.Get("kenticoBlogLevel")),
					NodeACLID = 1, // Default ACL ID
					NodeSiteID = siteId,
					NodeOwner = nodeOwnerId,
					NodeOrder = 100,
					NodeGUID = Guid.NewGuid(),
					NodeInheritPageLevels = "",
					NodeTemplateForAllCultures = true,
					NodeChildNodesCount = 0,
					NodeParentID = (root != null ? root.NodeID : 0)
				};

				context.CONTENT_Blog.AddObject(b);
				context.CMS_Tree.AddObject(blog);
				context.SaveChanges();

				AddDocument(context, blog, b.BlogID);
			}

			Console.WriteLine("Connecting categories, tags, and comments to posts. This may take a while.");
			foreach (CONTENT_BlogPost post in posts) {
				CMS_Document postDoc = ImportPost(context, blog, post);
				LinkCategoriesAndTags(context, postDoc, post);
				ImportComments(context, postDoc);
			}

			OrderPosts(context, blog);
		}

		/**
		 * Order the posts by date.
		 * 
		 * Orders the posts and months by date, so that they're in a sane order for a blog. Otherwise,
		 * it ends up ordering alphabetically (April 2008, April 2009, December 2008...). 
		 * 
		 * It's not perfect, since the sproc uses the DocumentModifiedDate field to order things,
		 * but it does a sufficient job. Anything that isn't ordered correctly is generally just
		 * one position off, which can easily be moved by the user.
		 * 
		 * This may be able to be deprecated, if we can build the correct ordering into the insert
		 * functions. At the moment, though, this does the job, and I'm running out of time to use
		 * this script for its original purpose.
		 * 
		 * @param entity context
		 * @param CMS_Tree blogTreeNode
		 */
		protected static void OrderPosts(kenticofreeEntities context, CMS_Tree blogTreeNode) {
			Console.WriteLine("Ordering blog months.");
			context.ExecuteStoreCommand("exec Proc_CMS_Tree_OrderDateAsc @NodeParentID={0}", blogTreeNode.NodeID);

			Console.WriteLine("Ording blog posts.");
			var blogMonths = (from t in context.CMS_Tree
							  where t.NodeParentID == blogTreeNode.NodeID
							  select t
							);

			foreach (CMS_Tree month in blogMonths) {
				context.ExecuteStoreCommand("exec Proc_CMS_Tree_OrderDateAsc @NodeParentID={0}",month.NodeID);
			}
		}
		
		/**
		 * Link categories and tags to a post.
		 * 
		 * This is combined into one function, because the Wordpress XML uses the same "category" tag
		 * for both categories and tags for a post, and just uses the "domain" attribute to distinguish
		 * between them.
		 * 
		 * @param entity context
		 * @param CMS_Document postDoc
		 * @param CONTENT_BlogPost post
		 */
		protected static void LinkCategoriesAndTags(kenticofreeEntities context, CMS_Document postDoc, CONTENT_BlogPost post) {
			
			var xml = (from c in wpxml.Descendants("item")
							  where Int32.Parse(c.Element(wpns + "post_id").Value) == post.BlogPostID
							  select c
							).Single();
			var categories = (from c in xml.Descendants("category")
							  select new {
								  Domain = c.Attribute("domain").Value,
								  Nicename = c.Attribute("nicename").Value
							  }
							);

			foreach (var cat in categories) {
				
				if (cat.Domain == "post_tag") {
					CMS_TagGroup tg = GetTagGroup(context);
					CMS_Tag tag = (from t in context.CMS_Tag
								   where t.TagName == cat.Nicename && t.TagGroupID == tg.TagGroupID
								   select t
								).SingleOrDefault();
					postDoc.CMS_Tag.Add(tag);
					if (String.IsNullOrEmpty(postDoc.DocumentTags)) {
						postDoc.DocumentTags = tag.TagName;
					}
					else {
						postDoc.DocumentTags = postDoc.DocumentTags + ", " + tag.TagName;
					}
					tag.TagCount++;
				}
				else if (cat.Domain == "category") {
					CMS_Category category = (from c in context.CMS_Category
											 where c.CategoryName == cat.Nicename
											 select c
											).SingleOrDefault();
					postDoc.CMS_Category.Add(category);
					category.CategoryCount++;
				}
			}
			context.SaveChanges();
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
			Regex consolidateDashes = new Regex("[-]{2}");

			/* We want to preserve the IDs of the Posts for linking, but EF won't let us turn on IDENTITY_INSERT
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
				string nodeAlias = consolidateDashes.Replace(forbidden.Replace(post.BlogPostTitle, "-"), "-");
				nodeAlias = (nodeAlias.Length > 50 ? nodeAlias.Substring(0, 50) : nodeAlias); // Truncate the alias to avoid SQL Server errors

				// Create the Tree Node for the post and add it in
				treeNode = new CMS_Tree() {
					NodeAliasPath = string.Format("{0}/{1}", month.NodeAliasPath, forbidden.Replace(post.BlogPostTitle, "-")),
					NodeName = post.BlogPostTitle,
					NodeAlias = nodeAlias,
					NodeClassID = blogClass.ClassID,
					NodeParentID = month.NodeID,
					NodeLevel = Int32.Parse(config.Get("kenticoBlogLevel")) + 2,
					NodeACLID = 1, // Default ACL ID
					NodeSiteID = siteId,
					NodeGUID = Guid.NewGuid(),
					NodeInheritPageLevels = "",
					NodeTemplateForAllCultures = true,
					NodeChildNodesCount = 0
				};

				CMS_User author = GetAuthor(context, post);
				treeNode.NodeOwner = author.UserID;

				context.CMS_Tree.AddObject(treeNode);
				treeNode.NodeOrder = GetNodeOrder(context, treeNode);
				month.NodeChildNodesCount++; // Increment the child nodes count, so the new post will display in the CMS
				blog.NodeChildNodesCount++; // Increment the blog's child nodes count, too.
				context.SaveChanges();
			}

			CMS_TagGroup tagGroup = GetTagGroup(context);

			// Create the document and add it into the database
			CMS_Document postDoc = AddDocument(context, treeNode, post.BlogPostID, tagGroup.TagGroupID);

			return postDoc;
		}

		/**
		 * Get the author from the XML to map the post to the user.
		 * 
		 * This allows for the "multiple authors" feature.
		 * 
		 * @return CMS_User
		 */
		protected static CMS_User GetAuthor(kenticofreeEntities context, CONTENT_BlogPost post) {
			var authorname = (from p in wpxml.Descendants("item")
							  where Int32.Parse(p.Element(wpns + "post_id").Value) == post.BlogPostID
							  select p
							).SingleOrDefault().Element(dc + "creator").Value;

			CMS_User kenticoUser = (from p in context.CMS_User
							   where p.UserName == authorname
							   select p
							).SingleOrDefault();

			return kenticoUser;
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

			CMS_Tree treeNode = null;
			// Find out the classID of the CMS.BlogMonth document type
			CMS_Class monthClass = context.CMS_Class.Where(x => x.ClassName == "CMS.BlogMonth").First();

			// If not, make a new one
			if (monthQuery.Any() == false) {
				CONTENT_BlogMonth month = new CONTENT_BlogMonth() {
					BlogMonthName = post.BlogPostDate.ToString("MMMM yyyy"),
					BlogMonthStartingDate = new DateTime(post.BlogPostDate.Year, post.BlogPostDate.Month, 01)
				};

				context.CONTENT_BlogMonth.AddObject(month);

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
							where b.BlogMonthID == month.BlogMonthID && t.NodeClassID == monthClass.ClassID
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
		 * @param bool hasTags
		 * @return CMS_Document
		 */
		protected static CMS_Document AddDocument(kenticofreeEntities context, CMS_Tree treeNode, int itemId, int tagGroupId = 0) {
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

			// Include the TagGroupID that the documents belong to. Used largely for blog entries.
			if (tagGroupId > 0) {
				doc.DocumentTagGroupID = tagGroupId;
			}

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
			for (int i = 0; i < hashBytes.Length; i++) {
				byte b = hashBytes[i];
				stringBuilder.Append(string.Format("{0:x2}", b));
			}

			return stringBuilder.ToString();
		}
	}
}